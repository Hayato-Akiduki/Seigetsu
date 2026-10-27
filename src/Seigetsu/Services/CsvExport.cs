using System.Globalization;
using System.Text;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>タスクと作業記録の CSV（v6 と同じ列）。</summary>
public static class CsvExport
{
    /// <summary>Excel で開いても文字化けしないよう、BOM 付き UTF-8 で書く（v6 と同じ）。</summary>
    public static void Write(string path, SeigetsuDatabase db) =>
        File.WriteAllText(path, Build(db), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

    public static string Build(SeigetsuDatabase db)
    {
        var csv = new StringBuilder();
        void Row(params object[] cells) =>
            csv.Append(string.Join(",", cells.Select(c => Quote(Convert.ToString(c, CultureInfo.InvariantCulture) ?? ""))))
               .Append("\r\n");

        Row("種類", "タイトル/日時", "完了/秒数", "優先度/モード", "締切", "カテゴリ");
        foreach (var t in db.AllTasks())
            Row("タスク", t.Title, t.Completed ? 1 : 0, t.Priority, t.DueDate, t.Category);
        foreach (var (startedAt, seconds, mode) in db.SessionRows())
            Row("作業記録", startedAt, seconds, mode, "", "");
        return csv.ToString();
    }

    // カンマ・ダブルクォート・改行を含むときだけ "..." で囲む（Python の csv モジュールと同じ）
    private static string Quote(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
}
