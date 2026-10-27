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
        new("music", "BGM", SymbolRegular.MusicNote224, () => new MusicPage()),
        new("settings", "設定", SymbolRegular.Settings24, () => new SettingsPage()),
    ];

    private readonly Dictionary<string, UserControl> _pages = [];

    // タイマーの表示更新とフェーズ終了の確認（v6 と同じ 0.5 秒ごと）
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };

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

        _tick.Tick += (_, _) => OnTick();
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

    private void ShowPage(string key) => NavList.SelectedIndex = _items.FindIndex(i => i.Key == key);

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

    /// <summary>タスクの追加（task が null）か編集の画面を開き、保存されたらデータベースに書く。</summary>
    public void OpenTaskDialog(TaskItem? task = null, string presetDue = "")
    {
        var db = App.Database;
        var dialog = new TaskDialog(task, db.Categories(), presetDue) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (task is null)
        {
            db.AddTask(dialog.ResultTitle, dialog.ResultPriority, dialog.ResultDue, dialog.ResultCategory);
            Notify($"タスク「{dialog.ResultTitle}」を追加しました。");
        }
        else
        {
            db.UpdateTask(task.Id, dialog.ResultTitle, dialog.ResultPriority, dialog.ResultDue, dialog.ResultCategory);
            Notify("タスクを更新しました。");
        }
        DataChanged();
    }

    /// <summary>付箋の追加（note が null）か編集の画面を開き、保存されたらデータベースに書く。</summary>
    public void OpenStickyDialog(StickyNote? note = null)
    {
        var dialog = new StickyDialog(note) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        if (note is null)
        {
            App.Database.AddSticky(dialog.ResultTitle, dialog.ResultBody);
            Notify("付箋を追加しました。");
        }
        else
        {
            App.Database.UpdateSticky(note.Id, dialog.ResultTitle, dialog.ResultBody);
            Notify("付箋を更新しました。");
        }
        DataChanged();
    }

    /// <summary>データが変わったとき、表示中の画面を作り直す（ほかの画面は開いたときに更新される）。</summary>
    public void DataChanged()
    {
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
        MiniMusic.Visibility = track is null ? Visibility.Collapsed : Visibility.Visible;
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
        _tick.Stop();
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

    // Ctrl+1〜9 で画面切り替え、Ctrl+N で新しいタスク、Ctrl+Q で終了（v6 と同じ）
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        if (e.Key == Key.N)
        {
            OpenTaskDialog();
            e.Handled = true;
        }
        else if (e.Key is >= Key.D1 and <= Key.D9)
        {
            var index = e.Key - Key.D1;
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
