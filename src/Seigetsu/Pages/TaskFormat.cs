using System.Windows.Media;
using Seigetsu.Services;

namespace Seigetsu.Pages;

/// <summary>データが変わったときに表示を作り直せる画面。</summary>
public interface IRefreshable
{
    void Refresh();
}

/// <summary>タスクの締切や優先度を、画面に出す文字と色にする（v6 の due_label と同じ書き方）。</summary>
public static class TaskFormat
{
    public static string Today => DateTime.Today.ToString("yyyy-MM-dd");

    public static string DueLabel(string due)
    {
        if (due == "")
            return "期限なし";
        if (due == Today)
            return "今日まで";
        if (!DateTime.TryParseExact(due, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var date))
            return due;
        return string.CompareOrdinal(due, Today) < 0
            ? $"{date.Month}/{date.Day} 期限切れ"
            : $"{date.Month}/{date.Day}まで";
    }

    public static bool IsOverdue(string due) => due != "" && string.CompareOrdinal(due, Today) < 0;

    public static Brush PriorityBrush(string priority) => ThemeService.Brush(priority switch
    {
        "高" => "DangerBrush",
        "低" => "SuccessBrush",
        _ => "WarningBrush",
    });

    /// <summary>期限切れは赤、今日までは青、それ以外は灰色。</summary>
    public static Brush DueBrush(string due, bool completed)
    {
        if (completed)
            return ThemeService.Brush("MutedBrush");
        if (IsOverdue(due))
            return ThemeService.Brush("DangerBrush");
        return ThemeService.Brush(due == Today ? "AccentBrush" : "MutedBrush");
    }
}
