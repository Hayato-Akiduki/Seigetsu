using System.ComponentModel;
using System.Media;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Seigetsu.Data;
using Seigetsu.Dialogs;
using Seigetsu.Pages;
using Seigetsu.Services;
using Wpf.Ui.Controls;

namespace Seigetsu;

/// <summary>左メニューの 1 項目。画面は初めて開いたときに作る。</summary>
public sealed record NavItem(string Key, string Label, SymbolRegular Icon, Func<UserControl> Create);

public partial class MainWindow : FluentWindow
{
    private readonly List<NavItem> _items =
    [
        new("home", "ホーム", SymbolRegular.Home24, () => new HomePage()),
        new("tasks", "タスク", SymbolRegular.TaskListSquareLtr24, () => new TasksPage()),
        new("timer", "集中タイマー", SymbolRegular.Timer24, () => new TimerPage()),
        new("notes", "メモ", SymbolRegular.Notepad24, () => new NotesPage()),
        new("calendar", "カレンダー", SymbolRegular.CalendarLtr24, () => new CalendarPage()),
        new("university", "大学", SymbolRegular.HatGraduation24, () => new UniversityPage()),
        new("lacs", "LACS", SymbolRegular.BookOpen24, () => new LacsPage()),
        new("stats", "分析", SymbolRegular.DataBarVertical24, () => new StatsPage()),
        new("stickies", "付箋", SymbolRegular.Note24, () => new StickiesPage()),
        new("board", "掲示板", SymbolRegular.Board24, () => new BoardPage()),
        new("music", "BGM", SymbolRegular.MusicNote224, () => new MusicPage()),
        new("recommend", "おすすめ", SymbolRegular.Sparkle24, () => new RecommendPage()),
        new("settings", "設定", SymbolRegular.Settings24, () => new SettingsPage()),
    ];

    private readonly Dictionary<string, UserControl> _pages = [];

    // 左メニューに今出している項目（「大学」は長崎大学モードのときだけ）
    private List<NavItem> _visible = [];

    // タイマーの表示更新とフェーズ終了の確認（v6 と同じ 0.5 秒ごと）
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // 1 日 1 回の仕事（自動バックアップと締切の通知）。起動の 5 秒後と、その後 10 分ごとに「今日はもう済んだか」を見る
    private readonly DispatcherTimer _daily = new() { Interval = TimeSpan.FromSeconds(5) };

    // 10・14・18 時の勉強の通知。1 分ごとに「今の枠をもう知らせたか」を見る
    private readonly DispatcherTimer _study = new() { Interval = TimeSpan.FromMinutes(1) };

    private bool? _miniRunning;

    // 集中ロック（タイマーが動いているあいだ、全画面で覆ってほかのアプリに移れなくする）
    private readonly FocusLock _focusLock = new();

    private readonly TrayIcon _tray;
    private bool _quitting;        // true のときは × でもタスクトレイに隠さず、本当に終わる
    private bool _trayHintShown;   // 「タスクトレイで動いています」は最初の 1 回だけ

    public MainWindow()
    {
        _tray = new TrayIcon(onOpen: ShowFromTray, onQuit: QuitApp);
        App.Updates.UpdateReady += OnUpdateReady;
        App.Nagasaki.Synced += OnNagasakiSynced;
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"v{version?.ToString(3)}（C# 版・開発中）";
        UpdateNavItems();
        NavList.SelectedIndex = 0;
        ApplySidebar(App.Database.GetSetting(SettingKeys.SidebarCollapsed, "0") == "1");
        ApplyBackground();

        _focusLock.StopRequested += OnFocusLockStop;
        _tick.Tick += (_, _) => OnTick();
        _daily.Tick += (_, _) =>
        {
            _daily.Interval = TimeSpan.FromMinutes(10);
            RunDailyJobs();
        };
        _daily.Start();
        _study.Tick += (_, _) => RemindStudy();
        _study.Start();
        _tick.Start();
        // 曲が終わって次の曲に進んだときなど、どの画面にいても小さな表示を合わせる
        App.Player.StateChanged += UpdateMiniTimer;
        UpdateMiniTimer();
        // Windows の通知のボタンが押されたら、ここで受け取る
        Toasts.SetHandler(OnToastAction);
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavItem item)
            return;
        // LACS のページの全画面のまま、ほかの画面に移ったら（Ctrl+数字など）、全画面をやめる
        if (_immersive && item.Key != "lacs")
            SetImmersive(false);
        if (!_pages.TryGetValue(item.Key, out var page))
        {
            page = item.Create();
            _pages[item.Key] = page;
        }
        PageHost.Content = page;
        // ページによって余白が違う（LACS は小さい）。全画面のあいだは、全画面の余白のまま
        if (!_immersive)
            ApplyPageMargin();
    }

    // ---- 左メニューの開閉 -----------------------------------------------
    private bool _sidebarCollapsed;

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        if (_immersive)
            SetImmersive(false);
        ApplySidebar(!_sidebarCollapsed);
        // 幅がせまくて自動で閉じているときに開いたのは、その場だけ（いつもの設定は変えない）
        if (!_narrow)
            App.Database.SetSetting(SettingKeys.SidebarCollapsed, _sidebarCollapsed ? "1" : "0");
    }

    // ---- 窓の幅 ---------------------------------------------------------
    // 画面の半分などのせまい幅では、左メニューを自動で閉じ、中身のまわりの余白を小さくする
    private const double NarrowWidth = 1000;
    private bool _narrow;

    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = ActualWidth < NarrowWidth;
        if (narrow == _narrow)
            return;
        _narrow = narrow;
        // 全画面のあいだは左メニューも余白も変えない（やめるときに合わせる）
        if (_immersive)
            return;
        ApplyPageMargin();
        ApplySidebar(narrow || App.Database.GetSetting(SettingKeys.SidebarCollapsed, "0") == "1");
    }

    private void ApplyPageMargin()
    {
        // LACS のページは中身を広く見せたいので、上はタイトルバー（× ボタンなど）の高さだけ、左右と下も小さく空ける
        if (PageHost.Content is LacsPage)
            PageHost.Margin = _narrow ? new Thickness(10, TitleBarSpace, 10, 10) : new Thickness(16, TitleBarSpace, 16, 12);
        else
            PageHost.Margin = _narrow ? new Thickness(20, 44, 20, 16) : new Thickness(44, 52, 44, 32);
    }

    // タイトルバーに重なると、そこにあるボタンが押せなくなる（つかんで窓を動かす所になる）ので、その高さ＋少しは空ける
    private double TitleBarSpace => (MainTitleBar.ActualHeight > 0 ? MainTitleBar.ActualHeight : 32) + 2;

    // ---- 全画面（LACS のページで PDF などを大きく見るとき） ----------------
    private bool _immersive;
    private WindowState _stateBeforeImmersive;

    /// <summary>左メニューを隠し、中身の余白を小さくして、窓を画面いっぱいにしているか。</summary>
    public bool IsImmersive => _immersive;

    /// <summary>全画面にする・やめる。やめるときは、左メニュー・余白・窓の大きさを元に戻す。</summary>
    public void SetImmersive(bool on)
    {
        if (on == _immersive)
            return;
        _immersive = on;
        if (on)
        {
            _stateBeforeImmersive = WindowState;
            SidebarPanel.Visibility = Visibility.Collapsed;
            SidebarColumn.Width = new GridLength(0);
            // 上は窓の × ボタンなどの分だけ空ける
            PageHost.Margin = new Thickness(8, TitleBarSpace, 8, 8);
            WindowState = WindowState.Maximized;
        }
        else
        {
            SidebarPanel.Visibility = Visibility.Visible;
            WindowState = _stateBeforeImmersive;
            ApplyPageMargin();
            ApplySidebar(_narrow || App.Database.GetSetting(SettingKeys.SidebarCollapsed, "0") == "1");
            if (_pages.TryGetValue("lacs", out var page))
                ((LacsPage)page).ExitFullScreen();
        }
    }

    /// <summary>閉じるとアイコンだけの細い幅にする（ロゴの文字・バージョン・タイマーの文字は隠し、開始ボタンは残す）。</summary>
    private void ApplySidebar(bool collapsed)
    {
        _sidebarCollapsed = collapsed;
        var hidden = collapsed ? Visibility.Collapsed : Visibility.Visible;
        SidebarColumn.Width = new GridLength(collapsed ? 76 : 232);
        Logo.Visibility = hidden;
        VersionText.Visibility = hidden;
        NavList.Tag = collapsed ? "collapsed" : "expanded";

        MiniText.Visibility = hidden;
        MiniBox.Padding = collapsed ? new Thickness(0, 4, 0, 4) : new Thickness(14, 8, 6, 8);
        Grid.SetColumn(MiniButton, collapsed ? 0 : 1);
        Grid.SetColumnSpan(MiniButton, collapsed ? 2 : 1);
        MiniButton.HorizontalAlignment = collapsed ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;

        SidebarToggleIcon.Symbol = collapsed ? SymbolRegular.PanelLeftExpand24 : SymbolRegular.PanelLeftContract24;
        var label = collapsed ? "メニューを開く" : "メニューを閉じる";
        SidebarToggle.ToolTip = $"{label}（Ctrl+B）";
        System.Windows.Automation.AutomationProperties.SetName(SidebarToggle, label);
        UpdateMiniTimer();
    }

    public void ShowPage(string key) => NavList.SelectedIndex = _visible.FindIndex(i => i.Key == key);

    /// <summary>
    /// 左メニューの項目を、長崎大学モードのオン・オフに合わせる。開いている画面はそのまま選び直す
    /// （「大学」を開いたままオフにしたときは、ホームに戻る）。
    /// </summary>
    public void UpdateNavItems()
    {
        var nagasaki = NagasakiUniversity.Enabled;
        var visible = _items.Where(i => i.Key is not ("university" or "lacs") || nagasaki).ToList();
        if (visible.Count == _visible.Count)
            return;
        var selected = (NavList.SelectedItem as NavItem)?.Key;
        _visible = visible;
        NavList.ItemsSource = _visible;
        if (selected is not null)
        {
            var index = _visible.FindIndex(i => i.Key == selected);
            NavList.SelectedIndex = index >= 0 ? index : 0;
        }
        if (!nagasaki)
        {
            _pages.Remove("university");
            if (_pages.Remove("lacs", out var lacs))
                ((LacsPage)lacs).Dispose();
        }
    }

    // ---- タスクトレイ ---------------------------------------------------
    /// <summary>タスクトレイに隠れている（または最小化している）窓を前に出す。</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        // ほかのアプリの後ろに回らないよう、一瞬だけ最前面にしてから戻す
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnUpdateReady(string version)
    {
        var message = $"新しい版 {version} の準備ができました。アプリを閉じると更新されます（設定画面から今すぐ更新もできます）。";
        Notify(message, ControlAppearance.Info, 8);
        if (!IsActive)
            ShowToast(ToastKind.Update, $"新しい版 {version} の準備ができました", "アプリを閉じると更新されます。",
                buttons: [new("今すぐ更新", "updateNow")], clickAction: "settings");
        DataChanged(); // 設定画面を開いていれば「今すぐ更新」を出す
    }

    /// <summary>× ボタンの設定にかかわらず、アプリを終わる（タスクトレイの「終了」や Ctrl+Q）。</summary>
    public void QuitApp()
    {
        _quitting = true;
        Close();
    }

    private bool _restartForUpdate;

    /// <summary>ダウンロード済みの新しい版に、いますぐ入れ替えて起動し直す（メモの保存などは先に済ませる）。</summary>
    public void UpdateNow()
    {
        _restartForUpdate = true;
        QuitApp();
    }

    /// <summary>
    /// タスクの追加（task が null）か編集の画面を開き、保存されたらデータベースに書く。
    /// onAdded は、新しく追加したとき画面を作り直す前に、その id で呼ばれる（掲示板で、作ってすぐ貼るため）。
    /// </summary>
    public void OpenTaskDialog(TaskItem? task = null, string presetDue = "", Action<long>? onAdded = null)
    {
        var db = App.Database;
        var dialog = new TaskDialog(task, db.Categories(), presetDue) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (task is null)
        {
            var id = db.AddTask(dialog.ResultTitle, dialog.ResultPriority, dialog.ResultDue, dialog.ResultCategory);
            onAdded?.Invoke(id);
            Notify($"タスク「{dialog.ResultTitle}」を追加しました。");
        }
        else
        {
            db.UpdateTask(task.Id, dialog.ResultTitle, dialog.ResultPriority, dialog.ResultDue, dialog.ResultCategory);
            Notify("タスクを更新しました。");
        }
        DataChanged();
    }

    /// <summary>
    /// 付箋の追加（note が null）か編集の画面を開き、保存されたらデータベースに書く。
    /// onAdded は、新しく追加したとき画面を作り直す前に、その id で呼ばれる。
    /// </summary>
    public void OpenStickyDialog(StickyNote? note = null, Action<long>? onAdded = null, Window? owner = null)
    {
        var dialog = new StickyDialog(note) { Owner = owner ?? this };
        if (dialog.ShowDialog() != true)
            return;

        if (note is null)
        {
            var id = App.Database.AddSticky(dialog.ResultTitle, dialog.ResultBody);
            App.Database.SetStickyColor(id, dialog.ResultColor);
            onAdded?.Invoke(id);
            Notify("付箋を追加しました。");
        }
        else
        {
            App.Database.UpdateSticky(note.Id, dialog.ResultTitle, dialog.ResultBody);
            App.Database.SetStickyColor(note.Id, dialog.ResultColor);
            Notify("付箋を更新しました。");
        }
        DataChanged();
    }

    /// <summary>データが変わったとき、表示中の画面を作り直す（ほかの画面は開いたときに更新される）。</summary>
    public void DataChanged()
    {
        App.DesktopStickies.Refresh(); // デスクトップの付箋も、中身・色を合わせる
        if (PageHost.Content is IRefreshable page)
            page.Refresh();
    }

    /// <summary>
    /// 外観（LIGHT / DARK / SYSTEM）をその場で切り替える。色は画面を作るときに決めている所もあるので、
    /// 表示中の画面は作り直す（ほかの画面は、次に開いたときに作り直される）。
    /// </summary>
    public void ApplyAppearance(string mode)
    {
        ThemeService.Apply(mode);
        DataChanged();
    }

    /// <summary>背景画像の設定を、窓全体と表示中の画面に合わせる。</summary>
    public void ApplyBackground()
    {
        var image = BackgroundImage.For(BackgroundPlace.Window);
        WindowBackground.Background = image is null ? null : BackgroundImage.Fill(image);
        WindowBackground.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        WindowVeil.Visibility = WindowBackground.Visibility;
        WindowVeil.Opacity = BackgroundImage.WindowVeil;
        if (PageHost.Content is IRefreshable page)
            page.Refresh();
    }

    private ClockWindow? _clock;

    /// <summary>時計を画面いっぱいに出す（もう出ていれば前に出すだけ）。</summary>
    public void ShowBigClock()
    {
        if (_clock is not null)
        {
            _clock.Activate();
            return;
        }
        _clock = new ClockWindow(this);
        _clock.Closed += (_, _) => _clock = null;
        _clock.Show();
    }

    // ---- 1 日 1 回の仕事 ---------------------------------------------------
    private void RunDailyJobs()
    {
        try
        {
            if (DailyBackup.RunIfNeeded(App.Database))
                DataChanged(); // 設定画面を開いていれば「いちばん新しいバックアップ」を直す
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException
                                          or Microsoft.Data.Sqlite.SqliteException)
        {
            // バックアップできなくても、アプリはそのまま使えるようにする（理由は記録しておく）
            try
            {
                File.AppendAllText(AppPaths.ErrorLogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 自動バックアップ: {error}{Environment.NewLine}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }
        RemindDueTasks();
    }

    /// <summary>
    /// 今日が締切のタスクや期限切れのタスクがあれば、1 日 1 回 Windows の通知で知らせる。
    /// その日の分を知らせたら、次の日まで知らせない。
    /// </summary>
    private void RemindDueTasks(bool snoozed = false)
    {
        var db = App.Database;
        if (db.GetSetting(SettingKeys.DueNotify, "1") != "1")
            return;
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        // 「1 時間後にもう一度」で呼ばれたときは、今日もう知らせていても知らせる
        if (!snoozed && db.GetSetting(SettingKeys.DueNotifiedOn, "") == today)
            return;
        var (overdue, dueToday) = db.DueCounts();
        if (overdue + dueToday == 0)
            return;

        var parts = new List<string>();
        if (dueToday > 0)
            parts.Add($"今日が締切のタスクが {dueToday} 件");
        if (overdue > 0)
            parts.Add($"期限切れのタスクが {overdue} 件");
        var message = string.Join("、", parts) + "あります。";
        // 締切の近いものから 3 件、名前を並べる
        var tasks = db.TodayTasks(8).Where(t => t.DueDate != "" && string.CompareOrdinal(t.DueDate, today) <= 0).ToList();
        var lines = tasks.Take(3).Select(t => t.DueDate == today
            ? $"{t.Title}（今日まで）"
            : $"{t.Title}（{DateTime.ParseExact(t.DueDate, "yyyy-MM-dd", null):M/d} まで）").ToList();
        ShowToast(ToastKind.Due, dueToday > 0 ? $"今日が締切のタスクが {dueToday} 件あります" : $"期限切れのタスクが {overdue} 件あります",
            dueToday > 0 && overdue > 0
                ? $"期限切れのタスクも {overdue} 件あります。{NotifyPhrases.DueBody(overdueOnly: false)}"
                : NotifyPhrases.DueBody(overdueOnly: dueToday == 0),
            lines, note: overdue + dueToday > lines.Count ? $"ほか {overdue + dueToday - lines.Count} 件" : null,
            buttons: [new("タスクを開く", "tasks"), new("1 時間後にもう一度", "snoozeDue")], clickAction: "tasks");
        if (IsVisible)
            Notify(message, ControlAppearance.Info, 8);
        db.SetSetting(SettingKeys.DueNotifiedOn, today);
    }

    // 勉強の通知を出す時刻
    private static readonly int[] StudyHours = [10, 14, 18];

    /// <summary>
    /// 10・14・18 時に、締切の近い課題があればそれを、なければ勉強をうながす通知を出す。
    /// スリープの復帰や起動が少し遅れたときのために、その時刻から 1 時間のあいだなら出す（1 つの枠で 1 回だけ）。
    /// </summary>
    private void RemindStudy()
    {
        var db = App.Database;
        if (db.GetSetting(SettingKeys.StudyNotify, "1") != "1")
            return;
        var now = DateTime.Now;
        var hour = StudyHours.FirstOrDefault(h => now >= now.Date.AddHours(h) && now < now.Date.AddHours(h + 1));
        if (hour == 0)
            return;
        var slot = $"{now:yyyy-MM-dd} {hour:00}";
        if (db.GetSetting(SettingKeys.StudyNotified, "") == slot)
            return;
        db.SetSetting(SettingKeys.StudyNotified, slot);

        // 集中タイマーで作業している最中は、じゃましない
        if (App.Timer.Running && !(App.Timer.IsPomodoro && App.Timer.Phase == TimerPhase.Break))
            return;

        var today = now.ToString("yyyy-MM-dd");
        var tasks = db.UpcomingTasks(days: 3);
        if (tasks.Count > 0)
        {
            var overdue = tasks.Count(t => string.CompareOrdinal(t.DueDate, today) < 0);
            var title = overdue > 0
                ? $"期限切れの課題が {overdue} 件あります"
                : $"締切が近い課題が {tasks.Count} 件あります";
            var lines = tasks.Take(3).Select(t => $"{t.Title}（{DueLabel(t.DueDate, now.Date)}）").ToList();
            ShowToast(ToastKind.Study, title, NotifyPhrases.TasksBody(hour), lines,
                note: tasks.Count > 3 ? $"ほか {tasks.Count - 3} 件" : null,
                buttons: [new("集中タイマーを始める", "startFocus"), new("タスクを開く", "tasks")], clickAction: "tasks");
        }
        else
        {
            var (title, body) = NotifyPhrases.Study(hour);
            ShowToast(ToastKind.Study, title, body, note: NotifyPhrases.StudyNote(db.WorkSecondsToday()),
                buttons: [new("集中タイマーを始める", "startFocus"), new("あとで", "dismiss")], clickAction: "timer");
        }
    }

    // 「今日まで」「明日まで」「11/7（土）まで」「期限切れ（11/1）」
    private static string DueLabel(string dueDate, DateTime today)
    {
        var due = DateTime.ParseExact(dueDate, "yyyy-MM-dd", null);
        var days = (due - today).Days;
        return days switch
        {
            < 0 => $"期限切れ・{due:M/d}",
            0 => "今日まで",
            1 => "明日まで",
            _ => $"{due:M/d}（{due.ToString("ddd", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"))}）まで",
        };
    }

    /// <summary>
    /// スクリーンショットを撮る（v6 と同じく、霽月の窓をいったん隠して、隠れきってから撮る）。
    /// </summary>
    public async void TakeScreenshot()
    {
        var wasVisible = IsVisible;
        Hide();
        await Task.Delay(400);
        string? path = null;
        string? error = null;
        try
        {
            path = Screenshot.Capture();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception
                                      or System.Runtime.InteropServices.ExternalException or ArgumentException)
        {
            error = e.Message;
        }
        if (wasVisible)
            ShowFromTray();
        if (path is not null)
            Notify($"スクリーンショットを保存しました。\n{path}", seconds: 5);
        else
            Notify($"スクリーンショットを保存できませんでした。\n{error}", ControlAppearance.Danger, 6);
    }

    private CalculatorWindow? _calculator;
    private CharCounterWindow? _counter;

    /// <summary>電卓を開く（もう開いていれば前に出すだけ）。</summary>
    public void OpenCalculator()
    {
        if (_calculator is null)
        {
            _calculator = new CalculatorWindow { Owner = this };
            _calculator.Closed += (_, _) => _calculator = null;
            _calculator.Show();
        }
        _calculator.Activate();
    }

    private NagasakiWindow? _nagasaki;

    /// <summary>LACS のページを開く。title を渡すと、その名前の授業を開く。</summary>
    public void ShowLacs(string? title = null)
    {
        ShowPage("lacs");
        if (title is not null && _pages.TryGetValue("lacs", out var page))
            ((LacsPage)page).ShowCourse(title);
    }

    /// <summary>LACS のページで、課題（授業の id と成績表の列の id）の画面を開く。</summary>
    public void ShowLacsAssignment(string courseId, string columnId)
    {
        ShowPage("lacs");
        if (_pages.TryGetValue("lacs", out var page))
            ((LacsPage)page).OpenAssignment(courseId, columnId);
    }

    /// <summary>LACS のページで、決まったアドレス（授業の連絡など）を開く。</summary>
    public void ShowLacsUrl(string url)
    {
        ShowPage("lacs");
        if (_pages.TryGetValue("lacs", out var page))
            ((LacsPage)page).OpenUrl(url);
    }

    /// <summary>長崎大学のログインの窓を開く（もう開いていれば前に出すだけ）。ログインが済むと自動で取り込む。</summary>
    public void OpenNagasakiLogin()
    {
        if (_nagasaki is null)
        {
            _nagasaki = new NagasakiWindow { Owner = this };
            _nagasaki.Closed += (_, _) => _nagasaki = null;
            _nagasaki.Show();
        }
        _nagasaki.Activate();
    }

    /// <summary>
    /// 大学の情報を今すぐ更新する（設定画面とカレンダーのボタンから）。ログインが切れていたら、ログインの窓を開く。
    /// </summary>
    public async void UpdateNagasakiNow()
    {
        if (_nagasaki is not null)
        {
            _nagasaki.Activate(); // ログインの途中なら、そちらで続けてもらう
            return;
        }
        Notify("大学の情報を更新しています…");
        var report = await App.Nagasaki.SyncAsync(userAsked: true);
        if (report.NeedsLogin)
            OpenNagasakiLogin();
    }

    // 自動更新のあと。ログインが切れたことは、続けて何度も知らせない（切れた最初の 1 回と、ボタンで頼まれたときだけ）
    private bool _nagasakiLoginNotified;

    private void OnNagasakiSynced(NagasakiReport report, bool userAsked)
    {
        DataChanged();
        // 休講・補講・教室変更（と、それを知らせていそうな新しい連絡）が見つかったら、Windows の通知でも知らせる
        if (report.ChangeAlerts is { Count: > 0 } changes)
        {
            var lines = changes.Take(3).ToList();
            ShowToast(ToastKind.Classes, $"授業の変更が {changes.Count} 件見つかりました", NotifyPhrases.ClassesBody(),
                lines, note: changes.Count > 3 ? $"ほか {changes.Count - 3} 件（大学のページで見られます）" : null,
                buttons: [new("大学のページを開く", "university"), new("LACS を開く", "lacs")], clickAction: "university");
            if (changes.Count > 3)
                lines.Add($"ほか {changes.Count - 3} 件（大学のページで見られます）");
            Notify("授業の変更が見つかりました。\n" + string.Join("\n", lines), ControlAppearance.Caution, 10);
            return;
        }
        if (report.Error is { } error && userAsked)
            Notify($"大学の情報を更新できませんでした。\n{error}", ControlAppearance.Danger, 6);
        else if (report.NeedsLogin && _nagasaki is null && (userAsked || !_nagasakiLoginNotified))
            Notify("長崎大学のサイトのログインが切れています。設定の「長崎大学モード」からログインしてください。",
                ControlAppearance.Caution, 8);
        else if (report.Added > 0)
            Notify($"LACS の課題を {report.Added} 件タスクに追加しました。");
        else if (userAsked && !report.NeedsLogin)
            Notify("大学の情報を更新しました。");
        _nagasakiLoginNotified = report.NeedsLogin;
    }

    /// <summary>文字数カウンターを開く（もう開いていれば前に出すだけ）。</summary>
    public void OpenCharCounter()
    {
        if (_counter is null)
        {
            _counter = new CharCounterWindow { Owner = this };
            _counter.Closed += (_, _) => _counter = null;
            _counter.Show();
        }
        _counter.Activate();
    }

    /// <summary>タスクと作業記録を CSV に書き出す（分析画面と設定画面から）。</summary>
    public void ExportCsv()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "CSVで書き出す",
            FileName = $"seigetsu_{DateTime.Now:yyyyMMdd}.csv",
            DefaultExt = ".csv",
            Filter = "CSV (*.csv)|*.csv",
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            CsvExport.Write(dialog.FileName, App.Database);
            Notify($"CSVを書き出しました。\n{dialog.FileName}", seconds: 5);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Notify($"CSVを書き出せませんでした。\n{error.Message}", ControlAppearance.Danger, 6);
        }
    }

    public void Notify(string message, ControlAppearance appearance = ControlAppearance.Success, int seconds = 3)
    {
        new Snackbar(SnackbarHost)
        {
            Title = message,
            Appearance = appearance,
            Icon = new SymbolIcon(appearance == ControlAppearance.Danger
                ? SymbolRegular.ErrorCircle24
                : SymbolRegular.CheckmarkCircle24),
            Timeout = TimeSpan.FromSeconds(seconds),
        }.Show();
    }

    // ---- 集中タイマー -------------------------------------------------
    /// <summary>タイマーを操作したあとに呼ぶ。recorded は作業記録が増えたかもしれないとき。</summary>
    public void TimerChanged(bool recorded = false)
    {
        _focusLock.Update(App.Timer, this);
        UpdateMiniTimer();
        if (recorded)
            DataChanged();
        else if (PageHost.Content is ITimerAware page)
            page.OnTimerTick();
    }

    private void OnTick()
    {
        var finished = App.Timer.Tick();
        // ポモドーロの作業が終わって休憩に入ったら、通知より先にロックを解く
        _focusLock.Update(App.Timer, this);
        if (finished is not null)
            OnPhaseFinished(finished.Value);
        UpdateMiniTimer();
        if (PageHost.Content is ITimerAware page)
            page.OnTimerTick();
    }

    private void OnPhaseFinished(TimerFinish finished)
    {
        // 本文は毎回少し違う言い方にする（アプリの中の帯と Windows の通知で同じものを使う）
        var (title, body, buttons) = finished switch
        {
            TimerFinish.Work => ("作業時間が終わりました",
                NotifyPhrases.WorkFinished(FocusTimer.LengthLabel(App.Timer.BreakSeconds)),
                new List<ToastButtonSpec> { new("休憩を始める", "startBreak"), new("休憩をとばす", "skipBreak") }),
            TimerFinish.Break => ("休憩が終わりました",
                NotifyPhrases.BreakFinished(FocusTimer.LengthLabel(App.Timer.WorkSeconds)),
                new List<ToastButtonSpec> { new("作業を始める", "startWork") }),
            _ => ($"タイマー（{FocusTimer.LengthLabel(App.Timer.CountdownSeconds)}）が終わりました",
                NotifyPhrases.CountdownFinished(),
                new List<ToastButtonSpec> { new("もう一度", "again") }),
        };
        Notify($"{title}。{body}", ControlAppearance.Success, 8);
        // ほかのアプリを使っているときやタスクトレイに隠れているときは、Windows の通知でも知らせる（音は通知が鳴らす）
        if (!IsActive)
        {
            ShowToast(ToastKind.Timer, title, body,
                note: NotifyPhrases.FocusNote(App.Database.WorkSecondsToday()), buttons: buttons, clickAction: "timer");
            if (IsVisible)
                FlashTaskbar();
        }
        else if (Toasts.SoundFor(ToastKind.Timer) != "silent")
        {
            SystemSounds.Asterisk.Play();
        }
        DataChanged();
    }

    // ---- Windows の通知 -----------------------------------------------
    /// <summary>
    /// Windows の通知（見出し・アイコン・ボタン・音つき）を出す。出せなかったときは、タスクトレイのバルーンで知らせる。
    /// </summary>
    public void ShowToast(ToastKind kind, string title, string body, IReadOnlyList<string>? lines = null,
        string? note = null, IReadOnlyList<ToastButtonSpec>? buttons = null, string clickAction = "open")
    {
        if (Toasts.Show(kind, title, body, lines, note, buttons, clickAction))
            return;
        var text = string.Join("\n", new[] { body }.Concat(lines ?? []).Append(note ?? "").Where(s => s != ""));
        _tray.Notify($"霽月 - {title}", text);
    }

    // 締切の「1 時間後にもう一度」
    private DispatcherTimer? _dueSnooze;

    /// <summary>Windows の通知や、そのボタンが押されたとき。</summary>
    private void OnToastAction(string action)
    {
        var timer = App.Timer;
        switch (action)
        {
            // タイマーのボタンは、窓を出さずにタイマーだけ動かす（状態が変わっていたら、窓を出すだけにする）
            case "startBreak" when timer.IsPomodoro && timer.Phase == TimerPhase.Break && !timer.Running:
            case "startWork" when timer.IsPomodoro && timer.Phase == TimerPhase.Work && !timer.Running:
            case "again" when timer.IsCountdown && !timer.Running:
                timer.Start();
                TimerChanged();
                break;
            case "skipBreak" when timer.IsPomodoro && timer.Phase == TimerPhase.Break && !timer.Running:
                timer.SkipPhase();
                timer.Start();
                TimerChanged();
                break;
            case "startFocus":
                // 止まっていれば今のモードのまま始める。もう動いていれば、タイマーの画面を出すだけ
                if (!timer.Running)
                {
                    timer.Start();
                    TimerChanged();
                    Notify("集中タイマーを始めました。");
                }
                else
                {
                    ShowFromTray();
                    ShowPage("timer");
                }
                break;
            case "snoozeDue":
                _dueSnooze?.Stop();
                _dueSnooze = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
                _dueSnooze.Tick += (_, _) =>
                {
                    _dueSnooze?.Stop();
                    _dueSnooze = null;
                    RemindDueTasks(snoozed: true);
                };
                _dueSnooze.Start();
                break;
            case "updateNow":
                UpdateNow();
                break;
            case "tasks" or "timer" or "lacs" or "settings":
                ShowFromTray();
                ShowPage(action);
                break;
            case "university":
                ShowFromTray();
                ShowPage(_visible.Any(i => i.Key == "university") ? "university" : "calendar");
                break;
            default:
                ShowFromTray();
                break;
        }
    }

    // ロック画面の「長押しで一時停止」
    private void OnFocusLockStop()
    {
        App.Timer.Pause();
        TimerChanged();
        Notify("タイマーを一時停止して、集中ロックを解きました。", ControlAppearance.Info);
    }

    private void UpdateMiniTimer()
    {
        var timer = App.Timer;
        MiniStatus.Text = $"{timer.ModeName} ・ {timer.Status()}";
        MiniTime.Text = timer.Display();
        if (_miniRunning != timer.Running)
        {
            _miniRunning = timer.Running;
            MiniIcon.Symbol = timer.Running ? SymbolRegular.Pause24 : SymbolRegular.Play24;
        }

        var track = App.Player.Current;
        MiniMusic.Visibility = track is null || _sidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        // メニューを閉じているときは、タイマーの時間をボタンにマウスを乗せると出す
        MiniButton.ToolTip = _sidebarCollapsed ? $"開始・一時停止（{MiniStatus.Text}　{MiniTime.Text}）" : "開始・一時停止";
        if (track is not null)
            MiniMusic.Text = $"♪ {track.Name}{(App.Player.Paused ? "（一時停止）" : "")}";
    }

    private void OnMiniMusicClick(object sender, MouseButtonEventArgs e) => ShowPage("music");

    // ---- BGM の曲の追加（「曲を追加」とドラッグ＆ドロップ） ----------
    /// <summary>mp3 / wav / ogg だけをプレイリストに足す（v6 と同じ）。</summary>
    public void AddMusic(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        var added = list
            .Where(p => MusicPlayer.Extensions.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant()))
            .Count(p => App.Database.AddTrack(p));
        if (added > 0)
        {
            App.Player.SetTracks(App.Database.GetPlaylist());
            Notify($"{added}曲をプレイリストに追加しました。");
            DataChanged();
        }
        else if (list.Count > 0)
        {
            var anyMusic = list.Any(p => MusicPlayer.Extensions.Contains(System.IO.Path.GetExtension(p).ToLowerInvariant()));
            Notify(anyMusic ? "その曲はもうプレイリストにあります。" : "追加できるのは mp3 / wav / ogg ファイルです。",
                anyMusic ? ControlAppearance.Info : ControlAppearance.Danger);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
            AddMusic(files);
    }

    private void OnMiniToggleClick(object sender, RoutedEventArgs e)
    {
        App.Timer.Toggle();
        TimerChanged();
    }

    private void OnMiniTimerClick(object sender, MouseButtonEventArgs e) => ShowPage("timer");

    protected override void OnClosing(CancelEventArgs e)
    {
        // × ボタンの設定が「タスクトレイに格納する」なら、終わらずに隠すだけ（タイマーや BGM は動いたまま）
        if (!_quitting && App.Database.GetSetting(SettingKeys.CloseAction, SettingKeys.CloseActionDefault) == "tray")
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                ShowToast(ToastKind.App, "タスクトレイで動いています", "タスクトレイのアイコンをクリックすると、再び開きます。");
            }
            base.OnClosing(e);
            return;
        }

        // 書きかけのメモを保存し、計測中の時間を記録してから終わる（v6 と同じ）
        foreach (var page in _pages.Values.OfType<ISavable>())
            page.Flush();
        App.Timer.Reset();
        _focusLock.Release(this);
        App.DesktopStickies.CloseAll(); // 出していた付箋は、次に起動したときにまた出る
        _tick.Stop();
        _daily.Stop();
        _study.Stop();
        App.Player.StateChanged -= UpdateMiniTimer;
        App.Player.Dispose();
        _tray.Dispose();
        App.Updates.UpdateReady -= OnUpdateReady;
        // 新しい版をダウンロード済みなら入れ替える（「今すぐ更新」なら起動し直す、そうでなければ閉じたあとで）
        if (_restartForUpdate)
            App.Updates.ApplyAndRestart();
        else
            App.Updates.ApplyOnExit();
        base.OnClosing(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    private void FlashTaskbar()
    {
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Window = new WindowInteropHelper(this).Handle,
            Flags = 0x2 | 0xC, // タスクバーを、窓が前に出るまで点滅
        };
        FlashWindowEx(ref info);
    }

    // Ctrl+1〜9 で画面切り替え（10 番目の画面は Ctrl+0）、Ctrl+N で新しいタスク、Ctrl+Q で終了（v6 と同じ）
    // F11 で時計を大画面に、Ctrl+B で左メニューの開閉
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11 && Keyboard.Modifiers == ModifierKeys.None)
        {
            ShowBigClock();
            e.Handled = true;
            return;
        }
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        if (e.Key == Key.B)
        {
            ToggleSidebar();
            e.Handled = true;
        }
        else if (e.Key == Key.N)
        {
            OpenTaskDialog();
            e.Handled = true;
        }
        else if (e.Key is >= Key.D0 and <= Key.D9)
        {
            var index = e.Key == Key.D0 ? 9 : e.Key - Key.D1;
            if (index < _visible.Count)
                NavList.SelectedIndex = index;
            e.Handled = true;
        }
        else if (e.Key == Key.Q)
        {
            QuitApp(); // v6 と同じく、Ctrl+Q はタスクトレイに隠さず終わる
            e.Handled = true;
        }
    }
}
