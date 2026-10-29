using Microsoft.Win32;

namespace Seigetsu.Services;

/// <summary>
/// Windows にサインインしたときに霽月を自動で起動する設定。
/// Windows の「スタートアップ」の一覧（いまのユーザーの Run キー）に霽月を入れたり外したりする。
/// </summary>
public static class AutoStart
{
    /// <summary>自動で起動したことが分かるように付ける印。</summary>
    public const string Argument = "--autostart";

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Seigetsu";

    /// <summary>今回の起動が、Windows の起動による自動の起動かどうか。</summary>
    public static bool LaunchedAtStartup { get; } =
        Environment.GetCommandLineArgs().Skip(1).Contains(Argument, StringComparer.OrdinalIgnoreCase);

    /// <summary>自動起動がオンになっているか。</summary>
    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    /// <summary>オン・オフを切りかえる。うまくいかなかったら false。</summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                // いま動いている霽月の場所を入れる（インストール版なら、更新しても場所は変わらない）
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                    return false;
                key.SetValue(ValueName, $"\"{exe}\" {Argument}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }
}
