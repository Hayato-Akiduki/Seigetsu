using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Seigetsu.Data;
using Seigetsu.Services;
using Wpf.Ui.Controls;

namespace Seigetsu.Pages;

/// <summary>
/// LACS の授業を霽月の中で見るページ（長崎大学モードのときだけ左メニューに出る）。
/// 左に登録している授業、右にその授業のページを出す。
/// ログインは長崎大学モードと同じもの（同じ WebView2 の保存場所）を使うので、もう一度ログインしなくてよい。
/// 配られた PDF は、ダウンロードになるものも含めて、このページの中（Edge の PDF 表示）で開く。
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

    private CoreWebView2? _core;
    private bool _started;

    // 開いたときに選ぶ授業（今日の授業から開いたとき）。名前で LACS の授業を探す
    private string? _courseHint;

    public LacsPage()
    {
        InitializeComponent();
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    /// <summary>名前が似ている授業を選んで開く。見つからなければ一覧のまま。</summary>
    // 開いたときに出すアドレス（大学ページの連絡から開いたとき）。準備がまだなら、授業の一覧を読んだあとに開く
    private string? _pendingUrl;

    /// <summary>決まったアドレスを開く。</summary>
    public void OpenUrl(string url)
    {
        if (_core is not null && CourseList.ItemsSource is not null)
            _core.Navigate(url);
        else
            _pendingUrl = url;
    }

    // 開いたときに出す課題（大学ページの未提出の課題から開いたとき）。準備がまだなら、授業の一覧を読んだあとに開く
    private (string Course, string Column)? _pendingAssignment;

    /// <summary>課題（授業の id と成績表の列の id）の画面を開く。課題の画面が分からなければ、授業のページを開く。</summary>
    public async void OpenAssignment(string courseId, string columnId)
    {
        if (_core is null || CourseList.ItemsSource is null)
        {
            _pendingAssignment = (courseId, columnId);
            return;
        }
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

    public void ShowCourse(string title)
    {
        _courseHint = title;
        if (CourseList.ItemsSource is IEnumerable<LacsCourse> courses)
            SelectHintedCourse(courses);
    }

    private void SelectHintedCourse(IEnumerable<LacsCourse> courses)
    {
        if (_courseHint is null)
            return;
        var match = NagasakiUniversity.FindCourse(courses, _courseHint);
        _courseHint = null;
        if (match is null)
            StatusText.Text = "LACS で同じ名前の授業が見つかりませんでした。左の一覧から選んでください。";
        else if (CourseList.SelectedItem is LacsCourse selected && selected == match)
            _core?.Navigate(match.Url); // もう選んである授業でも、授業のページに戻す
        else
            CourseList.SelectedItem = match;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 画面を移って戻ってくるたびに呼ばれるので、準備は最初の 1 回だけ
        if (_started)
            return;
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
            UpdateBigPdfButton();
        };
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
        try
        {
            var url = $"https://{NagasakiUniversity.LacsHost}";
            var cookies = await _core.CookieManager.GetCookiesAsync(url);
            using var client = new LacsClient(string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")),
                _core.Settings.UserAgent);
            var courses = await client.ReadCoursesAsync();
            ListMessage.Text = courses.Count == 0 ? "登録している授業が見つかりませんでした。" : "";
            CourseList.ItemsSource = courses;
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
            else if (_courseHint is null)
                _core.Navigate(NagasakiUniversity.LacsUrl);
            else
                SelectHintedCourse(courses);
        }
        catch (LacsLoginException)
        {
            // ログインが切れている。右側で LACS のログインをしてもらい、済んだら一覧を読み直す
            ListMessage.Text = "LACS のログインが切れています。右側で長大ID でログインすると、授業の一覧が出ます。";
            StatusText.Text = "";
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
        if (CourseList.SelectedItem is LacsCourse course && _core is not null)
            _core.Navigate(course.Url);
    }

    // ---- PDF などのファイル ---------------------------------------------
    private (string Course, string Content)? CurrentFilePage() =>
        _core?.Source is { } source && FilePage.Match(source) is { Success: true } m
            ? (m.Groups[1].Value, m.Groups[2].Value) : null;

    private void UpdateBigPdfButton()
    {
        BigPdfButton.Visibility = CurrentFilePage() is null ? Visibility.Collapsed : Visibility.Visible;
        BigPdfButton.IsEnabled = true;
    }

    private async void OnBigPdfClick(object sender, RoutedEventArgs e)
    {
        if (_core is null || CurrentFilePage() is not { } file)
            return;
        BigPdfButton.IsEnabled = false;
        StatusText.Text = "資料を開いています…";
        try
        {
            var cookies = await _core.CookieManager.GetCookiesAsync($"https://{NagasakiUniversity.LacsHost}");
            using var client = new LacsClient(string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")),
                _core.Settings.UserAgent);
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

    /// <summary>
    /// ダウンロードになるファイルのうち PDF は、霽月のフォルダに保存してから、このページの中で開く。
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
        var path = UniquePath(Path.Combine(AppPaths.LacsFilesDir, SafeName(name)));
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

    private static string SafeName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name == "" ? "file.pdf" : name;
    }

    /// <summary>同じ名前のファイルがあれば「名前 (2).pdf」のようにする。</summary>
    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;
        var dir = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
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
        if (CourseList.ItemsSource is null)
            await LoadCoursesAsync();
        else
            _core?.Reload();
    }

    private void OnListToggleClick(object sender, RoutedEventArgs e) => ToggleList();

    private void ToggleList() => ShowList(ListPanel.Visibility != Visibility.Visible);

    private void ShowList(bool show)
    {
        ListPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ListColumn.Width = show ? new GridLength(260) : new GridLength(0);
        ListToggleIcon.Symbol = show ? SymbolRegular.PanelLeftContract24 : SymbolRegular.PanelLeftExpand24;
    }

    private void OnFullScreenClick(object sender, RoutedEventArgs e) => SetFullScreen(!Main.IsImmersive);

    /// <summary>
    /// 全画面: 霽月の左メニューと授業の一覧を隠し、窓を画面いっぱいにする。false で元に戻す。
    /// ほかの画面に移ったときは、霽月の側（MainWindow）が元に戻し、ExitFullScreen で一覧も戻す。
    /// </summary>
    private void SetFullScreen(bool on)
    {
        if (on == Main.IsImmersive)
            return;
        Main.SetImmersive(on);
        ShowList(!on);
        FullScreenButton.Content = on ? "全画面をやめる" : "全画面";
    }

    /// <summary>霽月の側で全画面をやめたとき（ほかの画面に移ったときなど）に呼ばれる。</summary>
    public void ExitFullScreen()
    {
        ShowList(true);
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
    }
}
