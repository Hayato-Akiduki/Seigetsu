namespace Seigetsu.Services;

public static class Format
{
    /// <summary>秒数を「1時間05分」「12分」のように書く（v6 の format_duration と同じ）。</summary>
    public static string Duration(double seconds)
    {
        var minutes = (int)seconds / 60;
        var hours = minutes / 60;
        return hours > 0 ? $"{hours}時間{minutes % 60:00}分" : $"{minutes}分";
    }
}
