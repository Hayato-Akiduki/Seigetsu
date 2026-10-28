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
        new("stats", "分析", SymbolRegular.DataBarVertical24, () => new StatsPage()),
        new("stickies", "付箋", SymbolRegular.Note24, () => new StickiesPage()),
        new("board", "掲示板", SymbolRegular.Board24, () => new BoardPage()),
        new("music", "BGM", SymbolRegular.MusicNote224, () => new MusicPage()),
        new("settings", "設定", SymbolRegular.Settings24, () => new SettingsPage()),
    ];

    private readonly Dictionary<string, UserControl> _pages = [];

    // タイマーの表示更新とフェーズ終了の確認（v6 と同じ 0.5 秒ごと）
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

    // 1 日 1 回の仕事（自動バックアップと締切の通知）。起動の 5 秒後と、その後 10 分ごとに「今日はもう済んだか」を見る
    private readonly DispatcherTimer _daily = new() { Interval = TimeSpan.FromSeconds(5) };

    private bool? _miniRunning;

    private readonly TrayIcon _tray;
    private bool _quitting;        // true のときは × でもタスクトレイに隠さず、本当に終わる
    private bool _trayHintShown;   // 「タスクトレイで動いています」は最初の 1 回だけ

    public MainWindow()
    {
        _tray = new TrayIcon(onOpen: ShowFromTray, onQuit: QuitApp);
        App.Updates.UpdateReady += OnUpdateReady;
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"v{version?.ToString(3)}（C# 版・開発中）";
        NavList.ItemsSource = _items;
        NavList.SelectedIndex = 0;
        ApplySidebar(App.Database.GetSetting(SettingKeys.SidebarCollapsed, "0") == "1");
        ApplyBackground();

        _tick.Tick += (_, _) => OnTick();
        _daily.Tick += (_, _) =>
        {
            _daily.Interval = TimeSpan.FromMinutes(10);
            RunDailyJobs();
        };
        _daily.Start();
        _tick.Start();
        // 曲が終わって次の曲に進んだときなど、どの画面にいても小さな表示を合わせる
        App.Player.StateChanged += UpdateMiniTimer;
        UpdateMiniTimer();
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavItem item)
            return;
        if (!_pages.TryGetValue(item.Key, out var page))
        {
            page = item.Create();
            _pages[item.Key] = page;
        }
        PageHost.Content = page;
    }

    // ---- 左メニューの開閉 -----------------------------------------------
    private bool _sidebarCollapsed;

    private void OnSidebarToggleClick(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar()
    {
        ApplySidebar(!_sidebarCollapsed);
        App.Database.SetSetting(SettingKeys.SidebarCollapsed, _sidebarCollapsed ? "1" : "0");
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

    public void ShowPage(string key) => NavList.SelectedIndex = _items.FindIndex(i => i.Key == key);

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
            _tray.Notify("霽月 - アップデート", message);
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
    private void RemindDueTasks()
    {
        var db = App.Database;
        if (db.GetSetting(SettingKeys.DueNotify, "1") != "1")
            return;
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        if (db.GetSetting(SettingKeys.DueNotifiedOn, "") == today)
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
        _tray.Notify("霽月 - タスクの締切", message);
        if (IsVisible)
            Notify(message, ControlAppearance.Info, 8);
        db.SetSetting(SettingKeys.DueNotifiedOn, today);
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
        UpdateMiniTimer();
        if (recorded)
            DataChanged();
        else if (PageHost.Content is ITimerAware page)
            page.OnTimerTick();
    }

    private void OnTick()
    {
        var finished = App.Timer.Tick();
        if (finished is not null)
            OnPhaseFinished(finished.Value);
        UpdateMiniTimer();
        if (PageHost.Content is ITimerAware page)
            page.OnTimerTick();
    }

    private void OnPhaseFinished(TimerPhase finished)
    {
        var message = finished == TimerPhase.Work
            ? "作業時間が終わりました。少し休憩しましょう。"
            : "休憩が終わりました。次の作業を始めましょう。";
        Notify(message, ControlAppearance.Success, 8);
        SystemSounds.Asterisk.Play();
        // ほかのアプリを使っているときやタスクトレイに隠れているときは、Windows の通知でも知らせる
        if (!IsActive)
        {
            _tray.Notify("霽月 - 集中タイマー", message);
            if (IsVisible)
                FlashTaskbar();
        }
        DataChanged();
    }

    private void UpdateMiniTimer()
    {
        var timer = App.Timer;
        MiniStatus.Text = $"{(timer.IsPomodoro ? "ポモドーロ" : "ストップウォッチ")} ・ {timer.Status()}";
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
        if (!_quitting && App.Database.GetSetting(SettingKeys.CloseAction, "quit") == "tray")
        {
            e.Cancel = true;
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.Notify("霽月", "タスクトレイで動いています。アイコンをクリックすると再び開きます。");
            }
            base.OnClosing(e);
            return;
        }

        // 書きかけのメモを保存し、計測中の時間を記録してから終わる（v6 と同じ）
        foreach (var page in _pages.Values.OfType<ISavable>())
            page.Flush();
        App.Timer.Reset();
        App.DesktopStickies.CloseAll(); // 出していた付箋は、次に起動したときにまた出る
        _tick.Stop();
        _daily.Stop();
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
            if (index < _items.Count)
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
