namespace Seigetsu.Data;

/// <summary>保存先のパス。v6（Python 版）と同じ場所を使うので、データをそのまま引き継げる。</summary>
public static class AppPaths
{
    public static string DataDir { get; } = ResolveDataDir();

    public static string DatabasePath => Path.Combine(DataDir, "seigetsu.db");

    // v6 の error.log と混ざらないよう、C# 版は別のファイルに書く
    public static string ErrorLogPath => Path.Combine(DataDir, "error-v7.log");

    private static string ResolveDataDir()
    {
        // テスト用に保存先を切り替えられる（v6 と同じ環境変数）
        var overrideDir = Environment.GetEnvironmentVariable("SEIGETSU_DATA_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir))
            return overrideDir;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "akidesk");
    }
}

/// <summary>settings テーブルのキー（v6 と同じ名前）。</summary>
public static class SettingKeys
{
    public const string Appearance = "appearance_mode";   // LIGHT / DARK / SYSTEM
    public const string Background = "background_image";
    public const string City = "weather_city";
    public const string WorkMinutes = "pomodoro_work_min";
    public const string BreakMinutes = "pomodoro_break_min";
    public const string CountdownSeconds = "countdown_seconds";     // 普通のタイマーの長さ（秒。v7 だけの設定）
    public const string CloseAction = "close_action";     // quit / tray
    public const string Volume = "music_volume";
    public const string SidebarCollapsed = "sidebar_collapsed"; // 1 / 0（v7 だけの設定）
    public const string DueNotify = "due_notify";               // 1 / 0 締切の通知（v7 だけの設定）
    public const string DueNotifiedOn = "due_notified_on";      // 締切を最後に知らせた日（yyyy-MM-dd）
    // 背景画像を出す場所（v7 だけの設定。1 / 0）
    public const string BackgroundBanner = "background_on_banner"; // ホームの上帯
    public const string BackgroundWindow = "background_on_window"; // 窓全体
    public const string BackgroundClock = "background_on_clock";   // 時計の大画面
    public const string BackgroundStrength = "background_strength"; // 窓全体に敷いた画像の濃さ STRONG / NORMAL / WEAK
    // ホームの上帯に出すことば（v7 だけの設定）
    public const string BannerKinds = "banner_kinds";       // 出す種類（greeting,quote,season,tasks,custom をカンマで区切る）
    public const string BannerInterval = "banner_interval"; // 切り替える間隔（秒）
    public const string BannerCustom = "banner_custom";     // 自分で書いたことば（1 行に 1 つ）
}
