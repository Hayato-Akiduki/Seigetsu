using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Seigetsu.Data;
using Seigetsu.Services;
using Wpf.Ui.Controls;

namespace Seigetsu.Pages;

/// <summary>左の一覧の授業 1 つ。Sub は授業の番号と学期。</summary>
public sealed record CourseRow(LacsCourse Course, bool InTimetable)
{
    public string Name => Course.Name;

    public string Sub => string.Join("・", new[] { InTimetable ? "時間割にあり" : "", Course.Term, Course.Code }
        .Where(s => s != ""));
}

/// <summary>右の「課題」の 1 行。</summary>
public sealed record AssignmentRow(
    TaskItem Task, string Title, string DueText, Brush DueBrush, Brush TitleBrush, TextDecorationCollection? Decorations,
    SymbolRegular Icon, Brush IconBrush);

/// <summary>右の「連絡事項」の 1 行。Open のときは本文を出す。</summary>
public sealed record AnnouncementRow(AnnouncementItem Item, bool Open)
{
    public string Title => Item.Title;

    public string DateText => Item.Created.ToString("M/d");

    public string Body => Item.Body == "" ? "（本文はありません）" : Item.Body;

    public Visibility BodyVisibility => Open ? Visibility.Visible : Visibility.Collapsed;

    // まだ霽月で読んでいない連絡は太字
    public FontWeight TitleWeight => Item.Seen ? FontWeights.Normal : FontWeights.Bold;
}

/// <summary>
/// LACS の授業を霽月の中で見るページ（長崎大学モードのときだけ左メニューに出る）。
/// 左に登録している授業（ふだんは今の学期の分だけ。名前で探せる）、まん中にその授業のページ、
/// 右にその授業の課題・連絡事項・資料のまとめ保存を出す。
/// ログインは長崎大学モードと同じもの（同じ WebView2 の保存場所）を使うので、もう一度ログインしなくてよい。
/// 配られた PDF は、ダウンロードになるものも含めて、このページの中（Edge の PDF 表示）で開き、好きな場所に保存もできる。
/// 画面は一度作ったら残すので、ほかの画面に移って戻っても、見ていたページのまま。
/// </summary>
public partial class LacsPage : UserControl, IDisposable
{
    // LACS の資料 1 つの画面（例: /ultra/courses/_12345_1/file/_67890_1）
    private static readonly Regex FilePage = new(@"/ultra/courses/(_\d+_\d+)/file/(_\d+_\d+)", RegexOptions.Compiled);

    // 一時的に置いた PDF などは、これより古ければページを開くときに消す
    private static readonly TimeSpan KeepFiles = TimeSpan.FromDays(7);

    // このページの中で開くファイルの種類（ほかはふつうのダウンロードにする）
    private static readonly string[] ViewableTypes = ["application/pdf"];

    // 授業の一覧と「課題と連絡」の幅。境目をドラッグして変えられ、変えた幅は覚えておく
    private const double DefaultListWidth = 260;
    private const double DefaultInfoWidth = 300;
    private const double MinPanelWidth = 180;
    private const double MaxPanelWidth = 520;
    // これより細くドラッグしたら、その欄を隠す（上のボタンでまた出せる）
    private const double HideBelow = 120;

    private static double SavedWidth(string key, double fallback) =>
        double.TryParse(App.Database.GetSetting(key, ""), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var width)
            ? Math.Clamp(width, MinPanelWidth, MaxPanelWidth) : fallback;

    private static double ListWidth => SavedWidth(SettingKeys.LacsListWidth, DefaultListWidth);

    private static double InfoWidth => SavedWidth(SettingKeys.LacsInfoWidth, DefaultInfoWidth);

    private CoreWebView2? _core;
    private bool _started;
    private IReadOnlyList<CourseRow> _allCourses = [];
    private CourseRow? _shownCourse;
    // 本文を開いている連絡事項
    private readonly HashSet<string> _openAnnouncements = [];
    private bool _savingMaterials;

    // 開いたときに選ぶ授業（今日の授業から開いたとき）。名前で LACS の授業を探す
    private string? _courseHint;

    public LacsPage()
    {
        InitializeComponent();
        CurrentOnlyBox.IsChecked = App.Database.GetSetting(SettingKeys.LacsCurrentOnly, "1") == "1";
        ShowList(true);
        ShowInfo(App.Database.GetSetting(SettingKeys.LacsInfoPanel, "1") == "1");
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private IEnumerable<LacsCourse> Courses => _allCourses.Select(c => c.Course);

    // 開いたときに出すアドレス（大学ページの連絡から開いたとき）。準備がまだなら、授業の一覧を読んだあとに開く
    private string? _pendingUrl;

    /// <summary>決まったアドレスを開く。</summary>
    public void OpenUrl(string url)
    {
        if (_core is not null && _allCourses.Count > 0)
            _core.Navigate(url);
        else
            _pendingUrl = url;
    }

    // 開いたときに出す課題（大学ページの未提出の課題から開いたとき）。準備がまだなら、授業の一覧を読んだあとに開く
    private (string Course, string Column)? _pendingAssignment;

    /// <summary>課題（授業の id と成績表の列の id）の画面を開く。課題の画面が分からなければ、授業のページを開く。</summary>
    public async void OpenAssignment(string courseId, string columnId)
    {
        if (_core is null || _allCourses.Count == 0)
        {
            _pendingAssignment = (courseId, columnId);
            return;
        }
        if (_allCourses.FirstOrDefault(c => c.Course.Id == courseId) is { } row)
            ShowCourseInfo(row);
        StatusText.Text = "課題の画面を探しています…";
        string? url = null;
        try
        {
            using var client = await BrowserClientAsync();
            url = await client.AssignmentUrlAsync(courseId, columnId);
        }
        catch (Exception error) when (error is LacsLoginException or LacsApiException or System.Net.Http.HttpRequestException
                                          or System.Text.Json.JsonException or TaskCanceledException)
        {
            // 見つからなくても、授業のページは開く
        }
        if (url is null)
            StatusText.Text = "課題の画面が見つからなかったので、授業のページを開きました。";
        _core.Navigate(url ?? new LacsCourse(courseId, "", "").Url);
    }

    /// <summary>このページのブラウザのログイン（Cookie）で LACS を読む部品。</summary>
    private async Task<LacsClient> BrowserClientAsync()
    {
        var cookies = await _core!.CookieManager.GetCookiesAsync($"https://{NagasakiUniversity.LacsHost}");
        return new LacsClient(string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")), _core.Settings.UserAgent);
    }

    /// <summary>名前が似ている授業を選んで開く。見つからなければ一覧のまま。</summary>
    public void ShowCourse(string title)
    {
        _courseHint = title;
        if (_allCourses.Count > 0)
            SelectHintedCourse();
    }

    private void SelectHintedCourse()
    {
        if (_courseHint is null)
            return;
        var match = NagasakiUniversity.FindCourse(Courses, _courseHint);
        _courseHint = null;
        if (match is null)
        {
            StatusText.Text = "LACS で同じ名前の授業が見つかりませんでした。左の一覧から選んでください。";
            return;
        }
        SelectCourse(match.Id, navigate: true);
    }

    /// <summary>授業を一覧で選んで、授業のページを開く。今の絞り込みで見えなければ、絞り込みを外す。</summary>
    private void SelectCourse(string courseId, bool navigate)
    {
        if (_allCourses.FirstOrDefault(c => c.Course.Id == courseId) is not { } row)
            return;
        if (CourseList.Items.Cast<CourseRow>().All(c => c.Course.Id != courseId))
        {
            SearchBox.Text = "";
            CurrentOnlyBox.IsChecked = false;
            ApplyFilter();
        }
        var item = CourseList.Items.Cast<CourseRow>().First(c => c.Course.Id == courseId);
        if (CourseList.SelectedItem is CourseRow current && current.Course.Id == item.Course.Id)
        {
            if (navigate)
                _core?.Navigate(row.Course.Url); // もう選んである授業でも、授業のページに戻す
        }
        else
        {
            CourseList.SelectedItem = item; // OnCourseSelected で開く
        }
        CourseList.ScrollIntoView(item);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 画面を移って戻ってくるたびに呼ばれる。準備は最初の 1 回だけ。右の課題と連絡は、戻るたびに新しくする
        if (_started)
        {
            if (_shownCourse is { } shown)
                ShowCourseInfo(shown);
            return;
        }
        _started = true;
        CleanOldFiles();
        StatusText.Text = "準備しています…";
        try
        {
            await Browser.EnsureCoreWebView2Async(await App.Nagasaki.EnvironmentAsync());
        }
        catch (Exception error) when (error is WebView2RuntimeNotFoundException or System.Runtime.InteropServices.COMException
                                          or InvalidOperationException or ArgumentException)
        {
            StatusText.Text = "";
            ListMessage.Text = "ブラウザの部品（Microsoft Edge WebView2 ランタイム）が見つからないため開けませんでした。";
            return;
        }
        _core = Browser.CoreWebView2;
        _core.HistoryChanged += (_, _) => UpdateButtons();
        _core.DocumentTitleChanged += (_, _) => UpdateTitle();
        _core.SourceChanged += (_, _) =>
        {
            UpdateTitle();
            UpdateFileButtons();
        };
        // 読み込んでいる間は、上に細い帯を動かす
        _core.NavigationStarting += (_, _) => LoadingBar.Visibility = Visibility.Visible;
        _core.NavigationCompleted += (_, _) => LoadingBar.Visibility = Visibility.Hidden;
        // ページの中の「全画面」（LACS の PDF プレビューのボタンなど）を押したら、霽月も全画面にする
        _core.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (_core.ContainsFullScreenElement != Main.IsImmersive)
                SetFullScreen(_core.ContainsFullScreenElement);
        };
        // 新しいタブで開くリンク（資料など）も、このページの中で開く
        _core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            _core.Navigate(args.Uri);
        };
        _core.DownloadStarting += OnDownloadStarting;

        // 自動更新が残しているログインを、このページのブラウザにも渡す（アプリを起動し直してもログインし直さずに済む）
        await App.Nagasaki.ShareLoginWithAsync(_core);
        await LoadCoursesAsync();
    }

    /// <summary>長崎大学モードをオフにしたとき、ページを止める（動画や音が鳴り続けないように）。</summary>
    public void Dispose() => Browser.Dispose();

    // ---- 授業の一覧 -----------------------------------------------------
    private async Task LoadCoursesAsync()
    {
        if (_core is null)
            return;
        ListMessage.Text = "授業を読み込んでいます…";
        CourseList.ItemsSource = null;
        _allCourses = [];
        try
        {
            using var client = await BrowserClientAsync();
            var courses = await client.ReadCoursesAsync();
            // 時間割（NU-Web）にある授業は、学期の情報が分からなくても「今の学期」に入れる
            var inTimetable = App.Database.GetTimetable()
                .Select(s => NagasakiUniversity.FindCourse(courses, s.Title)?.Id)
                .Where(id => id is not null).ToHashSet();
            _allCourses = courses.Select(c => new CourseRow(c, inTimetable.Contains(c.Id))).ToList();
            ApplyFilter();
            StatusText.Text = courses.Count == 0 ? "" : "左の一覧から授業を選んでください。";

            if (_pendingAssignment is { } assignment)
            {
                _pendingAssignment = null;
                OpenAssignment(assignment.Course, assignment.Column);
            }
            else if (_pendingUrl is { } pending)
            {
                _pendingUrl = null;
                _core.Navigate(pending);
            }
            else if (_courseHint is not null)
                SelectHintedCourse();
            else if (App.Database.GetSetting(SettingKeys.LacsLastCourse, "") is { Length: > 0 } last
                     && _allCourses.Any(c => c.Course.Id == last))
                SelectCourse(last, navigate: true); // 前に見ていた授業から始める
            else
                _core.Navigate(NagasakiUniversity.LacsUrl);
        }
        catch (LacsLoginException)
        {
            // ログインが切れている。まん中で LACS のログインをしてもらい、済んだら一覧を読み直す
            ListMessage.Text = "LACS のログインが切れています。まん中で長大ID でログインすると、授業の一覧が出ます。";
            StatusText.Text = "";
            _signInLinkFollowed = false;
            _core.NavigationCompleted -= OnSignInNavigationCompleted;
            _core.NavigationCompleted += OnSignInNavigationCompleted;
            _core.Navigate(NagasakiUniversity.LacsHome);
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or LacsApiException
                                          or System.Text.Json.JsonException or TaskCanceledException)
        {
            ListMessage.Text = "授業の一覧を読み込めませんでした（" + error.Message + "）。上の ⟳ で読み込み直せます。";
            StatusText.Text = "";
        }
    }

    /// <summary>「今の学期だけ」と検索の文字で、一覧を絞る。時間割にある授業を上に出す。</summary>
    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var currentOnly = CurrentOnlyBox.IsChecked == true;
        var shown = _allCourses
            .Where(c => !currentOnly || c.Course.Current || c.InTimetable)
            .Where(c => query == "" || c.Course.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                     || c.Course.Code.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(c => c.InTimetable)
            .ThenBy(c => c.Course.Name, StringComparer.CurrentCulture)
            .ToList();
        var selected = CourseList.SelectedItem as CourseRow;
        CourseList.SelectionChanged -= OnCourseSelected;
        CourseList.ItemsSource = shown;
        if (selected is not null && shown.FirstOrDefault(c => c.Course.Id == selected.Course.Id) is { } again)
            CourseList.SelectedItem = again;
        CourseList.SelectionChanged += OnCourseSelected;

        CountText.Text = _allCourses.Count == 0 ? "" : $"{shown.Count} / {_allCourses.Count} 件";
        ListMessage.Text = _allCourses.Count == 0 ? "登録している授業が見つかりませんでした。"
            : shown.Count > 0 ? ""
            : query != "" ? "当てはまる授業がありません。"
            : "今の学期の授業が見つかりませんでした。「今の学期の授業だけ」を外すと、すべて出ます。";
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
            ApplyFilter();
    }

    private void OnCurrentOnlyClick(object sender, RoutedEventArgs e)
    {
        App.Database.SetSetting(SettingKeys.LacsCurrentOnly, CurrentOnlyBox.IsChecked == true ? "1" : "0");
        ApplyFilter();
    }

    // ログインの途中: LACS に戻ってきて、自分の情報が読めるようになったら一覧を読み直す
    private bool _signInLinkFollowed;

    private async void OnSignInNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (_core is null || !e.IsSuccess || !Uri.TryCreate(_core.Source, UriKind.Absolute, out var uri)
            || uri.Host != NagasakiUniversity.LacsHost)
            return;
        if (await NagasakiSync.LacsSignedInAsync(_core))
        {
            _core.NavigationCompleted -= OnSignInNavigationCompleted;
            // このページでログインし直したら、自動更新の方にも新しいログインを渡す
            await App.Nagasaki.AdoptBrowserLoginAsync(_core);
            await LoadCoursesAsync();
        }
        else if (!_signInLinkFollowed && await NagasakiSync.LacsSignInLinkAsync(_core) is { } link)
        {
            // LACS の最初のページ。「長大ID でログイン」のリンクを、押す代わりにたどる
            _signInLinkFollowed = true;
            _core.Navigate(link);
        }
    }

    private void OnCourseSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CourseList.SelectedItem is not CourseRow row || _core is null)
            return;
        _core.Navigate(row.Course.Url);
        App.Database.SetSetting(SettingKeys.LacsLastCourse, row.Course.Id);
        ShowCourseInfo(row);
    }

    // ---- 右の「課題と連絡」 ---------------------------------------------
    /// <summary>選んだ授業の課題（タスクに取り込んだもの）と連絡事項（自動更新で読んだもの）を出す。</summary>
    private void ShowCourseInfo(CourseRow row)
    {
        if (_shownCourse?.Course.Id != row.Course.Id)
        {
            _openAnnouncements.Clear();
            MaterialsText.Text = "";
            OpenMaterialsButton.Visibility = Directory.Exists(MaterialsFolder(row.Course)) ? Visibility.Visible : Visibility.Collapsed;
        }
        _shownCourse = row;
        InfoTitle.Text = row.Course.Name;
        InfoSub.Text = row.Sub;
        InfoBody.Visibility = Visibility.Visible;

        var muted = ThemeService.Brush("MutedBrush");
        var tasks = App.Database.LacsTasksOfCourse(row.Course.Id);
        AssignmentList.ItemsSource = tasks.Select(t => new AssignmentRow(
            t,
            // 取り込んだ題名は「課題名（授業名・23:59 締切）」なので、授業名の部分は省く
            ShortTitle(t.Title, row.Course.Name),
            t.Completed ? "提出済み・完了" : TaskFormat.DueLabel(t.DueDate) + DueTime(t.Title),
            TaskFormat.DueBrush(t.DueDate, t.Completed),
            t.Completed ? muted : ThemeService.Brush("TextBrush"),
            t.Completed ? TextDecorations.Strikethrough : null,
            t.Completed ? SymbolRegular.CheckmarkCircle24 : SymbolRegular.Circle24,
            t.Completed ? ThemeService.Brush("SuccessBrush") : TaskFormat.DueBrush(t.DueDate, false))).ToList();
        NoAssignmentText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        RefreshAnnouncements();
    }

    private void RefreshAnnouncements()
    {
        if (_shownCourse is not { } row)
            return;
        var items = App.Database.GetAnnouncements()
            .Where(a => a.Source == AnnouncementSource.Lacs && a.CourseId == row.Course.Id)
            .OrderByDescending(a => a.Created).Take(30)
            .Select(a => new AnnouncementRow(a, _openAnnouncements.Contains(a.Id))).ToList();
        AnnouncementList.ItemsSource = items;
        NoAnnouncementText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string ShortTitle(string title, string course)
    {
        var mark = title.LastIndexOf('（');
        return mark > 0 && title[(mark + 1)..].StartsWith(course, StringComparison.Ordinal) ? title[..mark] : title;
    }

    // 題名の終わりの「23:59 締切）」から、締切の時刻を拾う
    private static string DueTime(string title) =>
        Regex.Match(title, @"(\d{1,2}:\d{2}) 締切）$") is { Success: true } m ? $" {m.Groups[1].Value}" : "";

    private void OnAssignmentClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AssignmentRow row)
            return;
        // タスクのもとの課題は「授業の id/成績表の列の id」
        if (App.Database.LacsItemOfTask(row.Task.Id)?.Split('/') is [var course, var column])
            OpenAssignment(course, column);
    }

    private void OnAnnouncementClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not AnnouncementRow row)
            return;
        if (!_openAnnouncements.Remove(row.Item.Id))
        {
            _openAnnouncements.Add(row.Item.Id);
            if (!row.Item.Seen)
                App.Database.MarkAnnouncementSeen(row.Item.Id, row.Item.Source);
        }
        RefreshAnnouncements();
    }

    /// <summary>
    /// 境目をドラッグし終えたら、その幅を覚える。とても細くしたら、その欄を隠す（幅は前のまま覚えておく）。
    /// </summary>
    private void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        var isList = sender == ListSplitter;
        var width = (isList ? ListColumn : InfoColumn).ActualWidth;
        if (width < HideBelow)
        {
            if (isList)
                ShowList(false);
            else
            {
                ShowInfo(false);
                App.Database.SetSetting(SettingKeys.LacsInfoPanel, "0");
            }
            return;
        }
        width = Math.Clamp(width, MinPanelWidth, MaxPanelWidth);
        App.Database.SetSetting(isList ? SettingKeys.LacsListWidth : SettingKeys.LacsInfoWidth,
            width.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        (isList ? ListColumn : InfoColumn).Width = new GridLength(width);
    }

    private void OnInfoToggleClick(object sender, RoutedEventArgs e)
    {
        var show = InfoPanel.Visibility != Visibility.Visible;
        ShowInfo(show);
        App.Database.SetSetting(SettingKeys.LacsInfoPanel, show ? "1" : "0");
    }

    private void ShowInfo(bool show)
    {
        InfoPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        InfoColumn.Width = new GridLength(show ? InfoWidth : 0);
        // 隠しているときは境目もなくす（ページを端まで広げる）
        InfoSplitter.Visibility = InfoPanel.Visibility;
        InfoSplitColumn.Width = new GridLength(show ? 10 : 0);
        InfoToggleIcon.Symbol = show ? SymbolRegular.PanelRightContract24 : SymbolRegular.PanelRight24;
    }

    // ---- 資料のまとめ保存 -----------------------------------------------
    /// <summary>資料を保存する場所（ダウンロード\LACS\授業名）。LACS-MCP と同じ置き方。</summary>
    private static string MaterialsFolder(LacsCourse course) =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LACS",
            LacsClient.SafeName(course.Name));

    private async void OnSaveMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (_core is null || _shownCourse is not { } row || _savingMaterials)
            return;
        _savingMaterials = true;
        SaveMaterialsButton.IsEnabled = false;
        MaterialsText.Text = "資料を探しています…";
        var root = MaterialsFolder(row.Course);
        int saved = 0, skipped = 0, failed = 0;
        try
        {
            using var client = await BrowserClientAsync();
            var files = await client.ReadMaterialsAsync(row.Course.Id);
            if (files.Count == 0)
            {
                MaterialsText.Text = "この授業には、保存できるファイルが見つかりませんでした。";
                return;
            }
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                MaterialsText.Text = $"保存しています…（{i + 1} / {files.Count}）{file.Name}";
                var folder = Path.Combine([root, .. file.Folder.Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(LacsClient.SafeName)]);
                try
                {
                    if (await client.DownloadAsync(file, Path.Combine(folder, LacsClient.SafeName(file.Name))))
                        saved++;
                    else
                        skipped++;
                }
                catch (LacsLoginException)
                {
                    throw;
                }
                catch (Exception error) when (error is LacsApiException or System.Net.Http.HttpRequestException
                                                  or TaskCanceledException or IOException or UnauthorizedAccessException)
                {
                    failed++;
                    NagasakiLog.Write($"LACS: 資料を保存できませんでした（{error.Message}）");
                }
            }
            MaterialsText.Text = $"新しく {saved} 件保存しました" + (skipped > 0 ? $"（保存済み {skipped} 件）" : "")
                                 + (failed > 0 ? $"。{failed} 件は保存できませんでした" : "") + "。";
            OpenMaterialsButton.Visibility = Directory.Exists(root) ? Visibility.Visible : Visibility.Collapsed;
            if (saved > 0)
                Main.Notify($"「{row.Course.Name}」の資料を {saved} 件保存しました。");
        }
        catch (LacsLoginException)
        {
            MaterialsText.Text = "LACS のログインが切れています。上の ⟳ で読み込み直すと、ログインの画面になります。";
        }
        catch (Exception error) when (error is LacsApiException or System.Net.Http.HttpRequestException
                                          or System.Text.Json.JsonException or TaskCanceledException)
        {
            MaterialsText.Text = "資料を探せませんでした（" + error.Message + "）。";
        }
        finally
        {
            _savingMaterials = false;
            SaveMaterialsButton.IsEnabled = true;
        }
    }

    private void OnOpenMaterialsClick(object sender, RoutedEventArgs e)
    {
        if (_shownCourse is { } row && Directory.Exists(MaterialsFolder(row.Course)))
            Process.Start(new ProcessStartInfo { FileName = MaterialsFolder(row.Course), UseShellExecute = true });
    }

    // ---- PDF などのファイル ---------------------------------------------
    private (string Course, string Content)? CurrentFilePage() =>
        _core?.Source is { } source && FilePage.Match(source) is { Success: true } m
            ? (m.Groups[1].Value, m.Groups[2].Value) : null;

    /// <summary>今開いているのが、霽月が受け取って置いた PDF なら、そのファイル。</summary>
    private string? CurrentSavedPdf() =>
        _core?.Source is { } source && Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile
        && uri.LocalPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(uri.LocalPath)
            ? uri.LocalPath : null;

    private void UpdateFileButtons()
    {
        BigPdfButton.Visibility = CurrentFilePage() is null ? Visibility.Collapsed : Visibility.Visible;
        BigPdfButton.IsEnabled = true;
        SavePdfButton.Visibility = CurrentSavedPdf() is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnBigPdfClick(object sender, RoutedEventArgs e)
    {
        if (_core is null || CurrentFilePage() is not { } file)
            return;
        BigPdfButton.IsEnabled = false;
        StatusText.Text = "資料を開いています…";
        try
        {
            using var client = await BrowserClientAsync();
            if (await client.FileUrlAsync(file.Course, file.Content) is { } url)
            {
                // ファイルによって、そのまま表示されるものと、ダウンロードになるもの（OnDownloadStarting で開く）がある
                _core.Navigate(url);
                return;
            }
            StatusText.Text = "この資料にはファイルが見つかりませんでした。";
        }
        catch (LacsLoginException)
        {
            StatusText.Text = "LACS のログインが切れています。⟳ で読み込み直すと、ログインの画面になります。";
        }
        catch (Exception error) when (error is System.Net.Http.HttpRequestException or LacsApiException
                                          or System.Text.Json.JsonException or TaskCanceledException)
        {
            StatusText.Text = "資料を開けませんでした（" + error.Message + "）。";
        }
        BigPdfButton.IsEnabled = true;
    }

    private void OnSavePdfClick(object sender, RoutedEventArgs e) => SaveCurrentPdf();

    /// <summary>開いている PDF を、好きな場所にコピーして保存する（最初はダウンロードフォルダ）。</summary>
    private void SaveCurrentPdf()
    {
        if (CurrentSavedPdf() is not { } path)
            return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "PDF を保存",
            FileName = Path.GetFileName(path),
            DefaultExt = ".pdf",
            Filter = "PDF (*.pdf)|*.pdf",
            InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        };
        if (dialog.ShowDialog(Main) != true)
            return;
        try
        {
            File.Copy(path, dialog.FileName, overwrite: true);
            Main.Notify($"PDF を保存しました。\n{dialog.FileName}", seconds: 5);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Main.Notify($"PDF を保存できませんでした。\n{error.Message}", ControlAppearance.Danger, 6);
        }
    }

    /// <summary>
    /// ダウンロードになるファイルのうち PDF は、霽月のフォルダに置いてから、このページの中で開く（「この PDF を保存」で好きな場所に保存できる）。
    /// ほかの種類（Word や PowerPoint など）は、ふだんどおり「ダウンロード」フォルダに保存する。
    /// </summary>
    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        var download = e.DownloadOperation;
        var name = Path.GetFileName(e.ResultFilePath);
        var isPdf = ViewableTypes.Contains(download.MimeType, StringComparer.OrdinalIgnoreCase)
                    || name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);
        if (!isPdf)
            return;

        Directory.CreateDirectory(AppPaths.LacsFilesDir);
        var path = LacsClient.FreePath(Path.Combine(AppPaths.LacsFilesDir, LacsClient.SafeName(name == "" ? "file.pdf" : name)));
        e.ResultFilePath = path;
        e.Handled = true; // Edge のダウンロードの吹き出しは出さない
        StatusText.Text = $"{Path.GetFileName(path)} を開いています…";
        download.StateChanged += (_, _) =>
        {
            if (download.State == CoreWebView2DownloadState.Completed)
                _core?.Navigate(new Uri(path).AbsoluteUri);
            else if (download.State == CoreWebView2DownloadState.Interrupted)
                StatusText.Text = "ファイルを開けませんでした（" + download.InterruptReason + "）。";
        };
    }

    private static void CleanOldFiles()
    {
        try
        {
            if (!Directory.Exists(AppPaths.LacsFilesDir))
                return;
            foreach (var file in new DirectoryInfo(AppPaths.LacsFilesDir).GetFiles())
            {
                if (DateTime.Now - file.LastWriteTime > KeepFiles)
                    file.Delete();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // 開いている途中のファイルなどは、次の機会に消す
        }
    }

    // ---- 操作 ---------------------------------------------------------
    private void UpdateButtons()
    {
        BackButton.IsEnabled = _core?.CanGoBack == true;
        ForwardButton.IsEnabled = _core?.CanGoForward == true;
    }

    private void UpdateTitle()
    {
        if (_core is null)
            return;
        var title = _core.DocumentTitle;
        if (Uri.TryCreate(_core.Source, UriKind.Absolute, out var uri) && uri.IsFile)
            title = Path.GetFileName(uri.LocalPath);
        StatusText.Text = title;
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => _core?.GoBack();

    private void OnForwardClick(object sender, RoutedEventArgs e) => _core?.GoForward();

    private async void OnReloadClick(object sender, RoutedEventArgs e)
    {
        // 一覧が出ていなければ、一覧から読み直す
        if (_allCourses.Count == 0)
            await LoadCoursesAsync();
        else
            _core?.Reload();
    }

    private void OnListToggleClick(object sender, RoutedEventArgs e) => ToggleList();

    private void ToggleList() => ShowList(ListPanel.Visibility != Visibility.Visible);

    private void ShowList(bool show)
    {
        ListPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ListColumn.Width = show ? new GridLength(ListWidth) : new GridLength(0);
        ListSplitter.Visibility = ListPanel.Visibility;
        ListSplitColumn.Width = new GridLength(show ? 10 : 0);
        ListToggleIcon.Symbol = show ? SymbolRegular.PanelLeftContract24 : SymbolRegular.PanelLeftExpand24;
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(!Main.IsImmersive);

    // 全画面にする前に「課題と連絡」を出していたか（全画面をやめたら元に戻す）
    private bool _infoBeforeFullScreen;

    /// <summary>
    /// 全画面: 霽月の左メニューと授業の一覧・課題と連絡を隠し、窓を画面いっぱいにする。false で元に戻す。
    /// ほかの画面に移ったときは、霽月の側（MainWindow）が元に戻し、ExitFullScreen で一覧も戻す。
    /// </summary>
    private void SetFullScreen(bool on)
    {
        if (on == Main.IsImmersive)
            return;
        if (on)
            _infoBeforeFullScreen = InfoPanel.Visibility == Visibility.Visible;
        Main.SetImmersive(on);
        ShowList(!on);
        ShowInfo(!on && _infoBeforeFullScreen);
        FullScreenButton.Content = on ? "全画面をやめる" : "全画面";
    }

    /// <summary>霽月の側で全画面をやめたとき（ほかの画面に移ったときなど）に呼ばれる。</summary>
    public void ExitFullScreen()
    {
        ShowList(true);
        ShowInfo(_infoBeforeFullScreen || App.Database.GetSetting(SettingKeys.LacsInfoPanel, "1") == "1");
        FullScreenButton.Content = "全画面";
    }

    private void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (_core?.Source is not { } source || !Uri.TryCreate(source, UriKind.Absolute, out var uri))
            return;
        // 保存した PDF はそのファイルを、ほかは今のページを開く
        Process.Start(new ProcessStartInfo { FileName = uri.IsFile ? uri.LocalPath : uri.AbsoluteUri, UseShellExecute = true });
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+B は霽月の左メニューの開閉に使うので、ここでは使わない
        if (e.Key == Key.F11)
        {
            SetFullScreen(!Main.IsImmersive);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Main.IsImmersive)
        {
            SetFullScreen(false);
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _core?.Reload();
            e.Handled = true;
        }
        else if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control && CurrentSavedPdf() is not null)
        {
            SaveCurrentPdf();
            e.Handled = true;
        }
    }
}
