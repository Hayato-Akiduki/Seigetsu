using System.Globalization;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>
/// 毎日の自動バックアップ。1 日 1 回、データベースを backups-v7 フォルダに写し、新しい方から 7 日分だけ残す。
/// </summary>
public static class DailyBackup
{
    public const int Keep = 7;
    private const string Prefix = "seigetsu-";

    public static string Folder => Path.Combine(AppPaths.DataDir, "backups-v7");

    /// <summary>今日の分がまだなければ作る。作ったら true。</summary>
    public static bool RunIfNeeded(SeigetsuDatabase db)
    {
        var path = Path.Combine(Folder, $"{Prefix}{DateTime.Today:yyyy-MM-dd}.db");
        if (File.Exists(path))
            return false;
        Directory.CreateDirectory(Folder);
        // 途中で止まっても半端なファイルが「今日の分」として残らないよう、別の名前で作ってから名前を変える
        var temp = path + ".tmp";
        File.Delete(temp);
        db.BackupTo(temp);
        File.Move(temp, path);
        DeleteOld();
        return true;
    }

    /// <summary>いちばん新しいバックアップの日付。まだ無ければ null。</summary>
    public static DateTime? Latest =>
        Files().Select(DateOf).FirstOrDefault(d => d is not null);

    private static void DeleteOld()
    {
        foreach (var old in Files().Skip(Keep))
        {
            try
            {
                File.Delete(old);
            }
            catch (IOException)
            {
                // 消せなければ、次の日にまた試す
            }
        }
    }

    // 新しい順（ファイル名に日付が入っているので、名前の逆順）
    private static IEnumerable<string> Files() =>
        Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, $"{Prefix}*.db").OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            : [];

    private static DateTime? DateOf(string path) =>
        DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path)[Prefix.Length..], "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
