using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Seigetsu.Data;
using Seigetsu.Services;

namespace Seigetsu.Pages;

/// <summary>大学ページの「今日の授業」1 行分。Badge は「授業中」「次」（どちらでもなければ空）。</summary>
public sealed record UniversityClassView(
    ClassSlot Slot, string PeriodText, string TimeText, Visibility DetailVisibility, Visibility RoomVisibility,
    string Badge, Visibility BadgeVisibility, Brush Bar, double Opacity);

/// <summary>大学ページの課題 1 行分。</summary>
public sealed record UniversityAssignmentView(TaskItem Task, string DueText, Brush DueBrush);

/// <summary>大学ページの LACS の連絡 1 行分。SpokenName は読み上げソフトに読ませる文。</summary>
public sealed record UniversityAnnouncementView(
    AnnouncementItem Item, string DateText, Visibility UnseenVisibility, FontWeight TitleWeight, string SpokenName,
    Brush SourceBrush, string SourceName, string ChangeHint, Visibility ChangeHintVisibility);

/// <summary>
/// 「今日」の「授業の変更」の 1 行。NU-Web の休講補講（Change）か、授業の変更を知らせていそうな連絡（Announcement）。
/// Tag は「休講」「教室変更」「オンライン」など。
/// </summary>
public sealed record UniversityChangeView(string Tag, Brush TagBrush, string Text, string SubText, Visibility SubVisibility,
    AnnouncementItem? Announcement);

/// <summary>長崎大学モードのときだけ左メニューに出るページ。今日の授業と、LACS の未提出の課題をまとめて見る。</summary>
public partial class UniversityPage : UserControl, IRefreshable
{
    private static readonly CultureInfo Japanese = new("ja-JP");

    // 「授業中」「次」の印を時間に合わせて動かす（1 分ごと）
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMinutes(1) };

    public UniversityPage()
    {
        InitializeComponent();
        // 時計では授業の印だけ動かす（連絡の表を作り直すと、選んでいた行が外れるため）
        _clock.Tick += (_, _) => RefreshClasses();
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Nagasaki.Changed += OnNagasakiChanged;
        _clock.Start();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Nagasaki.Changed -= OnNagasakiChanged;
        _clock.Stop();
    }

    private void OnNagasakiChanged() => Dispatcher.BeginInvoke(Refresh);

    // 幅がせまいときは、課題を授業の下に回す
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = ActualWidth < 720;
        Gap.Width = new GridLength(narrow ? 0 : 16);
        SecondColumn.Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        SecondRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(AssignmentsPanel, narrow ? 2 : 1);
        Grid.SetColumn(AssignmentsPanel, narrow ? 0 : 2);
        AssignmentsPanel.Margin = new Thickness(0, narrow ? 12 : 0, 0, 0);
        LayoutAnnouncements(narrow);
    }

    // ---- 操作 ---------------------------------------------------------
    private void OnSyncClick(object sender, RoutedEventArgs e) => Main.UpdateNagasakiNow();

    private void OnNuWebClick(object sender, RoutedEventArgs e) => OpenInBrowser(NagasakiUniversity.NuWebUrl);

    private void OnLacsClick(object sender, RoutedEventArgs e) => Main.ShowLacs();

    private void OnClassClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenClass(sender);

    private void OnClassKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
        {
            OpenClass(sender);
            e.Handled = true;
        }
    }

    private void OpenClass(object sender)
    {
        if (((FrameworkElement)sender).DataContext is UniversityClassView view)
            Main.ShowLacs(view.Slot.Title);
    }

    private static void OpenInBrowser(string url) =>
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });

    // ---- 表示 ---------------------------------------------------------
    public void Refresh()
    {
        SyncButton.IsEnabled = !App.Nagasaki.IsRunning;
        SyncButton.Content = App.Nagasaki.IsRunning ? "更新しています…" : "今すぐ更新";
        StatusText.Text = "前回の更新：" + NagasakiSync.LastStatus;
        RefreshChanges();
        RefreshClasses();
        RefreshAssignments();
        ReloadAnnouncements();
        ShowView(_source);
    }

    private void RefreshClasses()
    {
        var now = DateTime.Now;
        ClassesTitle.Text = "今日の授業　" + now.ToString("M月d日（ddd）", Japanese);

        IReadOnlyList<ClassSlot> classes = now.DayOfWeek == DayOfWeek.Sunday ? [] : App.Database.GetTimetable((int)now.DayOfWeek);
        if (classes.Count == 0)
        {
            ClassesSub.Text = "";
            NoClassesText.Text = !HasAnyTimetable()
                ? "時間割がまだ読み込まれていません。\n右上の「今すぐ更新」で NU-Web から読み込めます。"
                : "今日は授業がありません。";
            NoClassesText.Visibility = Visibility.Visible;
            Classes.ItemsSource = null;
            return;
        }
        NoClassesText.Visibility = Visibility.Collapsed;

        // 授業中のコマと、これから始まる最初のコマに印を付ける。終わったコマは薄くする
        var clock = now.TimeOfDay;
        var current = classes.FirstOrDefault(s => Span(s) is { } t && t.Start <= clock && clock < t.End);
        var next = current is null ? classes.FirstOrDefault(s => Span(s) is { } t && clock < t.Start) : null;
        var remaining = classes.Count(s => Span(s) is not { } t || clock < t.End);
        ClassesSub.Text = remaining == 0 ? $"{classes.Count}コマ（今日の授業は終わりました）"
            : $"{classes.Count}コマ（残り {remaining}コマ）";

        var accent = ThemeService.Brush("AccentBrush");
        var line = ThemeService.Brush("LineBrush");
        // NU-Web の休講補講で、今日のこのコマに休講・教室変更があれば、印を付け、教室は新しいものにする
        var todayChanges = App.Database.GetClassChanges(now.Date).Where(c => c.Date == now.Date).ToList();
        Classes.ItemsSource = classes.Select(original =>
        {
            var s = original;
            var change = todayChanges.FirstOrDefault(c => c.Period == s.Period && s.Codes.Split(',').Contains(c.Code));
            if (change is { Kind: ClassChange.RoomChanged } && change.Room != "")
                s = s with { Room = change.Room };
            var badge = change is { Kind: ClassChange.Cancelled or ClassChange.RoomChanged } ? change.Kind
                : original == current ? "授業中" : original == next ? "次" : "";
            var done = change is { Kind: ClassChange.Cancelled } || (Span(s) is { } t && t.End <= clock);
            return new UniversityClassView(
                s, $"{s.Period}限", NagasakiUniversity.TimeOf(s),
                s.Detail == "" ? Visibility.Collapsed : Visibility.Visible,
                s.Room == "" ? Visibility.Collapsed : Visibility.Visible,
                badge, badge == "" ? Visibility.Collapsed : Visibility.Visible,
                done ? line : accent, done ? 0.55 : 1.0);
        }).ToList();
    }

    private static bool HasAnyTimetable() => App.Database.GetTimetable().Count > 0;

    // 「授業の変更」に出す連絡は、この日数のあいだに出たものだけ
    private const int ChangeNoticeDays = 14;

    /// <summary>
    /// 「今日」の上の「授業の変更」の欄。NU-Web の休講補講（今日から先）と、授業の変更を知らせていそうな最近の連絡を並べる。
    /// どちらも無ければ欄ごと隠す。
    /// </summary>
    private void RefreshChanges()
    {
        var japanese = Japanese;
        var rows = new List<UniversityChangeView>();
        foreach (var c in App.Database.GetClassChanges(DateTime.Today))
        {
            var room = c.Kind != ClassChange.Cancelled && c.Room != "" ? $"教室：{c.Room}" : "";
            rows.Add(new UniversityChangeView(c.Kind, ChangeBrush(c.Kind),
                $"{c.Date.ToString("M/d（ddd）", japanese)} {c.Period}限　{c.Subject}",
                room, room == "" ? Visibility.Collapsed : Visibility.Visible, null));
        }
        var since = DateTime.Today.AddDays(-ChangeNoticeDays);
        foreach (var a in App.Database.GetAnnouncements().Where(a => a.Created >= since))
        {
            if (NagasakiUniversity.ClassChangeHint(a.Title, a.Body) is not { } hint)
                continue;
            var site = a.Source == AnnouncementSource.NuWeb ? "NU-Web" : "LACS";
            rows.Add(new UniversityChangeView(hint, ChangeBrush(hint), a.Title,
                $"{site}の連絡・{a.CourseName}・{a.Created.ToString("M/d（ddd）", japanese)}", Visibility.Visible, a));
        }
        ChangesPanel.Visibility = rows.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ChangesList.ItemsSource = rows;
    }

    // 休講は赤、教室変更・オンライン・日時の変更は黄、補講は緑
    private static Brush ChangeBrush(string kind) => ThemeService.Brush(kind switch
    {
        ClassChange.Cancelled => "DangerBrush",
        ClassChange.Makeup => "SuccessBrush",
        _ => "WarningBrush",
    });

    // 「授業の変更」の連絡を押したら、その連絡のタブに移って開く。NU-Web の休講補講なら、NU-Web の画面をブラウザで開く
    private void OnChangeClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenChange(sender);

    private void OnChangeKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
        {
            OpenChange(sender);
            e.Handled = true;
        }
    }

    private void OpenChange(object sender)
    {
        if (((FrameworkElement)sender).DataContext is not UniversityChangeView view)
            return;
        if (view.Announcement is not { } a)
        {
            OpenInBrowser($"https://{NagasakiUniversity.NuWebHost}{NuWebClient.ClassChangePath}");
            return;
        }
        ShowView(a.Source == AnnouncementSource.NuWeb ? AnnouncementSource.NuWeb : AnnouncementSource.Lacs);
        if (AnnouncementList.ItemsSource is IEnumerable<UniversityAnnouncementView> shown
            && shown.FirstOrDefault(v => v.Item.Id == a.Id) is { } row)
        {
            AnnouncementList.SelectedItem = row;
            AnnouncementList.ScrollIntoView(row);
        }
    }

    /// <summary>「8:50～10:20」を (始まり, 終わり) にする。読めなければ null。</summary>
    private static (TimeSpan Start, TimeSpan End)? Span(ClassSlot slot)
    {
        var parts = NagasakiUniversity.TimeOf(slot).Split('～', '~', '-', '－');
        if (parts.Length == 2
            && TimeSpan.TryParse(parts[0].Trim(), CultureInfo.InvariantCulture, out var start)
            && TimeSpan.TryParse(parts[1].Trim(), CultureInfo.InvariantCulture, out var end))
            return (start, end);
        return null;
    }

    private void OnAssignmentClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenAssignment(sender);

    private void OnAssignmentKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space)
        {
            OpenAssignment(sender);
            e.Handled = true;
        }
    }

    // 課題は「授業の id/成績表の列の id」で残してある。それで LACS のページに課題の画面を開いてもらう
    private void OpenAssignment(object sender)
    {
        if (((FrameworkElement)sender).DataContext is not UniversityAssignmentView view
            || App.Database.LacsItemOfTask(view.Task.Id)?.Split('/') is not [var course, var column])
            return;
        Main.ShowLacsAssignment(course, column);
    }

    private void RefreshAssignments()
    {
        var tasks = App.Database.OpenLacsTasks();
        AssignmentsSub.Text = tasks.Count > 0 ? $"{tasks.Count}件（提出すると、次の更新で完了になります）" : "";
        NoAssignmentsText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var today = DateTime.Today;
        Assignments.ItemsSource = tasks.Select(t =>
        {
            if (!DateTime.TryParse(t.DueDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
                return new UniversityAssignmentView(t, "締切なし", ThemeService.Brush("MutedBrush"));
            var days = (due.Date - today).Days;
            var text = days switch
            {
                < 0 => $"{due:M/d} 締切（{-days}日すぎています）",
                0 => "今日が締切",
                1 => "明日が締切",
                _ => $"{due.ToString("M/d（ddd）", Japanese)} 締切（あと {days}日）",
            };
            var brush = ThemeService.Brush(days <= 1 ? "DangerBrush" : days <= 3 ? "AccentBrush" : "MutedBrush");
            return new UniversityAssignmentView(t, text, brush);
        }).ToList();
    }

    // ---- お知らせ（LACS の連絡事項と NU-Web の掲示） ----------------------
    private const string AllCourses = "すべて";

    // いま見ているタブ。null なら「今日」、それ以外は出どころ（AnnouncementSource）
    private string? _source;
    private IReadOnlyList<AnnouncementItem> _announcements = [];
    private bool _fillingAnnouncements; // 表を作り直している間は、選んだときの処理をしない

    private void OnTodayTabClick(object sender, RoutedEventArgs e) => ShowView(null);

    private void OnLacsTabClick(object sender, RoutedEventArgs e) => ShowView(AnnouncementSource.Lacs);

    private void OnNuWebTabClick(object sender, RoutedEventArgs e) => ShowView(AnnouncementSource.NuWeb);

    private bool IsNuWebView => _source == AnnouncementSource.NuWeb;

    /// <summary>「今日」「LACS の連絡」「NU-Web の連絡」を切り替える（source が null なら「今日」）。</summary>
    public void ShowView(string? source)
    {
        var changed = source != _source;
        _source = source;
        var today = source is null ? Visibility.Visible : Visibility.Collapsed;
        ClassesPanel.Visibility = today;
        AssignmentsPanel.Visibility = today;
        AnnouncementsView.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        static Wpf.Ui.Controls.ControlAppearance Look(bool on) =>
            on ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Secondary;
        TodayTab.Appearance = Look(source is null);
        LacsTab.Appearance = Look(source == AnnouncementSource.Lacs);
        NuWebTab.Appearance = Look(source == AnnouncementSource.NuWeb);
        if (source is null)
            return;
        AnnouncementsTitle.Text = IsNuWebView ? "NU-Web の連絡" : "LACS の連絡";
        // 一覧の題名の横にも、出どころの色の線を出す
        AnnouncementsTitleBar.Background = ThemeService.Brush(IsNuWebView ? "SuccessBrush" : "AccentBrush");
        if (changed)
        {
            // タブを変えたら、授業・分類の絞り込みと、選んでいた行は戻す
            _fillingAnnouncements = true;
            CourseFilter.SelectedItem = null;
            AnnouncementList.SelectedItem = null;
            _fillingAnnouncements = false;
            ReloadAnnouncements();
        }
    }

    /// <summary>残してあるお知らせを読み直して表を作る。選んでいたお知らせと絞り込みはそのまま。</summary>
    private void ReloadAnnouncements()
    {
        _announcements = App.Database.GetAnnouncements();
        // タブの名前に、まだ読んでいない数を出す
        var unseenLacs = _announcements.Count(a => !a.Seen && a.Source != AnnouncementSource.NuWeb);
        var unseenNuWeb = _announcements.Count(a => !a.Seen && a.Source == AnnouncementSource.NuWeb);
        LacsTab.Content = unseenLacs > 0 ? $"LACS の連絡（{unseenLacs}）" : "LACS の連絡";
        NuWebTab.Content = unseenNuWeb > 0 ? $"NU-Web の連絡（{unseenNuWeb}）" : "NU-Web の連絡";
        MarkAllButton.IsEnabled = _announcements.Any(a => !a.Seen && SourceShown(a));

        // 授業・分類の絞り込みの選択肢（選んでいたものは残す）。いま見ているタブの出どころの分だけ
        CourseFilterLabel.Text = IsNuWebView ? "ジャンル" : "授業";
        var chosen = CourseFilter.SelectedItem as string ?? AllCourses;
        var courses = new List<string> { AllCourses };
        courses.AddRange(_announcements.Where(SourceShown).Select(a => a.CourseName).Where(n => n != "")
            .Distinct().Order(StringComparer.CurrentCulture));
        _fillingAnnouncements = true;
        CourseFilter.ItemsSource = courses;
        CourseFilter.SelectedItem = courses.Contains(chosen) ? chosen : AllCourses;
        _fillingAnnouncements = false;
        FillAnnouncements();
    }

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (!_fillingAnnouncements && IsLoaded)
            FillAnnouncements();
    }

    private bool SourceShown(AnnouncementItem a) =>
        _source is not null && (a.Source == AnnouncementSource.NuWeb) == IsNuWebView;

    private void FillAnnouncements()
    {
        var course = CourseFilter.SelectedItem as string ?? AllCourses;
        var words = SearchBox.Text.Trim();
        var shown = _announcements
            .Where(SourceShown)
            .Where(a => course == AllCourses || a.CourseName == course)
            .Where(a => UnseenOnly.IsChecked != true || !a.Seen)
            .Where(a => words == "" || a.Title.Contains(words, StringComparison.CurrentCultureIgnoreCase)
                                    || a.Body.Contains(words, StringComparison.CurrentCultureIgnoreCase))
            .Select(ToView).ToList();

        var mine = _announcements.Where(SourceShown).ToList();
        var unseen = mine.Count(a => !a.Seen);
        var site = IsNuWebView ? "NU-Web" : "LACS";
        AnnouncementsSub.Text = mine.Count == 0 ? "" : $"{mine.Count}件（まだ読んでいないもの {unseen}件）・過去 60 日の分";
        NoAnnouncementsText.Text = mine.Count == 0
            ? $"{site} の連絡はまだ読み込まれていません。\n右上の「今すぐ更新」で読み込めます。"
            : "この条件に合う連絡はありません。";
        NoAnnouncementsText.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var selectedId = (AnnouncementList.SelectedItem as UniversityAnnouncementView)?.Item.Id;
        _fillingAnnouncements = true;
        AnnouncementList.ItemsSource = shown;
        AnnouncementList.SelectedItem = shown.FirstOrDefault(v => v.Item.Id == selectedId);
        _fillingAnnouncements = false;
        if (AnnouncementList.SelectedItem is null)
            ShowDetail(null);
    }

    private static UniversityAnnouncementView ToView(AnnouncementItem a)
    {
        // 出どころの色: LACS は青、NU-Web は緑（行の左端の線）
        var nuWeb = a.Source == AnnouncementSource.NuWeb;
        var source = nuWeb ? "NU-Web" : "LACS";
        // 授業の変更（休講・教室変更など）を知らせていそうなら、題名の前に札を付ける
        var hint = NagasakiUniversity.ClassChangeHint(a.Title, a.Body);
        var date = a.Created.Date == DateTime.Today ? a.Created.ToString("今日 H:mm")
            : a.Created.Year == DateTime.Today.Year ? a.Created.ToString("M/d（ddd）", Japanese)
            : a.Created.ToString("yyyy/M/d");
        return new UniversityAnnouncementView(a, date,
            a.Seen ? Visibility.Hidden : Visibility.Visible,
            a.Seen ? FontWeights.Normal : FontWeights.Bold,
            (a.Seen ? "" : "未読 ") + $"{source} {date} {a.CourseName} {hint} {a.Title}",
            ThemeService.Brush(nuWeb ? "SuccessBrush" : "AccentBrush"), source,
            hint ?? "", hint is null ? Visibility.Collapsed : Visibility.Visible);
    }

    private void OnAnnouncementSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingAnnouncements)
            return;
        var view = AnnouncementList.SelectedItem as UniversityAnnouncementView;
        ShowDetail(view?.Item);
        if (view is null || view.Item.Seen)
            return;
        // 開いたら読んだことにする。表の印と件数も合わせる（選んだ行はそのまま）
        App.Database.MarkAnnouncementSeen(view.Item.Id);
        ReloadAnnouncements();
    }

    private AnnouncementItem? _detail;

    private void ShowDetail(AnnouncementItem? item)
    {
        _detail = item;
        var nuWeb = item?.Source == AnnouncementSource.NuWeb;
        DetailTitle.Text = item?.Title ?? "連絡を選ぶと、ここに本文が出ます";
        DetailMeta.Text = item is null ? ""
            : nuWeb
                // NU-Web の掲示は、ジャンル（CourseName）と差出人（CourseId に入れてある）
                ? $"NU-Web　{item.CourseName}　{item.CourseId}　{item.Created.ToString("yyyy年M月d日（ddd） H:mm", Japanese)}"
                : $"LACS　{item.CourseName}　{item.Created.ToString("yyyy年M月d日（ddd） H:mm", Japanese)}";
        DetailSourceBar.Background = ThemeService.Brush(nuWeb ? "SuccessBrush" : "AccentBrush");
        DetailSourceBar.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        LinkText.SetText(DetailBody, item is null ? "" : item.Body == "" ? "（本文はありません）" : item.Body);
        DetailBody.ScrollToHome();
        // LACS は霽月の LACS のページで、NU-Web はふだんのブラウザで開く
        OpenInLacsButton.Content = nuWeb ? "NU-Web でこのお知らせを開く" : "LACS でこの授業の連絡を開く";
        OpenInLacsButton.IsEnabled = item is not null && (!nuWeb || item.Url != "");
    }

    // 「すべて読んだことにする」は、いま見ているタブ（LACS か NU-Web）の分だけ
    private void OnMarkAllClick(object sender, RoutedEventArgs e)
    {
        if (_source is null)
            return;
        App.Database.MarkAnnouncementSeen(null, _source);
        ReloadAnnouncements();
    }

    private void OnOpenInLacsClick(object sender, RoutedEventArgs e)
    {
        if (_detail is null)
            return;
        if (_detail.Source == AnnouncementSource.NuWeb)
            OpenInBrowser(_detail.Url);
        else
            Main.ShowLacsUrl(_detail.Url != "" ? _detail.Url
                : $"https://{NagasakiUniversity.LacsHost}/ultra/courses/{_detail.CourseId}/announcements");
    }

    // 幅がせまいときは、本文を表の下に回す
    private void LayoutAnnouncements(bool narrow)
    {
        DetailGap.Width = new GridLength(narrow ? 0 : 16);
        DetailColumn.Width = narrow ? new GridLength(0) : new GridLength(2, GridUnitType.Star);
        DetailRow.Height = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(DetailPanel, narrow ? 1 : 0);
        Grid.SetColumn(DetailPanel, narrow ? 0 : 2);
        DetailPanel.Margin = new Thickness(0, narrow ? 12 : 0, 0, 0);
    }
}
