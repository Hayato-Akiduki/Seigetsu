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
    public const string CloseAction = "close_action";     // quit / tray
    public const string Volume = "music_volume";
    public const string SidebarCollapsed = "sidebar_collapsed"; // 1 / 0（v7 だけの設定）
    public const string DueNotify = "due_notify";               // 1 / 0 締切の通知（v7 だけの設定）
    public const string DueNotifiedOn = "due_notified_on";      // 締切を最後に知らせた日（yyyy-MM-dd）
}
