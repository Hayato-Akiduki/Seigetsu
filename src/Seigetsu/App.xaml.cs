using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Seigetsu.Data;
using Seigetsu.Services;

namespace Seigetsu;

public partial class App : Application
{
    public static SeigetsuDatabase Database { get; private set; } = null!;

    /// <summary>集中タイマーはアプリに 1 つだけ。どの画面に移っても動き続ける。</summary>
    public static FocusTimer Timer { get; private set; } = null!;

    /// <summary>BGM プレイヤーもアプリに 1 つだけ。ほかの画面に移っても流れ続ける。</summary>
    public static MusicPlayer Player { get; private set; } = null!;

    /// <summary>自動更新（インストールした霽月だけで動く）。</summary>
    public static UpdateService Updates { get; private set; } = null!;

    /// <summary>デスクトップに出している付箋の窓。</summary>
    public static DesktopStickies DesktopStickies { get; private set; } = null!;

    /// <summary>天気（ホームに出す）。</summary>
    public static WeatherService Weather { get; private set; } = null!;

    /// <summary>長崎大学モードの自動更新（時間割と LACS の課題）。</summary>
    public static NagasakiSync Nagasaki { get; private set; } = null!;

    private SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        // すでに動いていれば、そちらの窓を出して、こちらは何もせずに終わる
        _instance = new SingleInstance(AppPaths.DataDir);
        if (!_instance.IsFirst)
        {
            _instance.SignalFirstInstance();
            Shutdown();
            return;
        }

        // 日付の表示などを日本語にする（WPF は何もしないと英語になる）
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ja-JP")));

        Database = new SeigetsuDatabase(AppPaths.DatabasePath);
        Timer = new FocusTimer(Database);
        // 音量は v6 と同じく "0.50" のような形で保存されている
        var volume = double.TryParse(Database.GetSetting(SettingKeys.Volume, "0.5"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.5;
        Player = new MusicPlayer(volume);
        Player.SetTracks(Database.GetPlaylist());
        ThemeService.Apply(Database.GetSetting(SettingKeys.Appearance, "LIGHT"));

        Updates = new UpdateService();
        DesktopStickies = new DesktopStickies();
        Weather = new WeatherService();
        Nagasaki = new NagasakiSync();

        var main = new MainWindow();
        if (AutoStart.LaunchedAtStartup)
        {
            // Windows の起動で自動に立ち上がったときは、作業のじゃまにならないよう小さくしておく
            main.WindowState = WindowState.Minimized;
            main.ShowActivated = false;
            main.Show();
            // × でタスクトレイに隠す設定なら、タスクトレイだけに置いておく
            if (Database.GetSetting(SettingKeys.CloseAction, "quit") == "tray")
                main.Hide();
        }
        else
        {
            main.Show();
        }
        // 前回デスクトップに出していた付箋を、同じ場所に出し直す
        DesktopStickies.Refresh();
        Weather.Start();
        Nagasaki.Start();
        _instance.ListenForShow(Dispatcher, main.ShowFromTray);
        Updates.Start(Dispatcher);

        // 「Windows に合わせる」のときは、Windows 側で明るい／暗いを切り替えたらすぐについていく
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General)
            return;
        Dispatcher.BeginInvoke(() =>
        {
            if (Database.GetSetting(SettingKeys.Appearance, "LIGHT") == "SYSTEM" && Current.MainWindow is Seigetsu.MainWindow main)
                main.ApplyAppearance("SYSTEM");
        });
    }

    // Windows のサインアウトやシャットダウンのときは、タスクトレイに隠さずに終わる
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        if (Current.MainWindow is Seigetsu.MainWindow main)
            main.QuitApp();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // SystemEvents は静的なイベントなので、終わるときに外しておく
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.AppendAllText(AppPaths.ErrorLogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch
        {
            // ログが書けなくても、下のメッセージは出す
        }
        MessageBox.Show($"予期しないエラーが起きました。\n\n{e.Exception.Message}", "霽月", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
