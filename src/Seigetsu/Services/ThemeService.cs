using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Seigetsu.Data;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Seigetsu.Services;

/// <summary>
/// 明るい／暗い外観の切り替え。色は (明るい, 暗い) の組。
/// 見た目は「線を減らし、うすい影と広い余白で見せる」落ち着いた作り。色は墨色を中心に、藍色を少しだけ使う。
/// </summary>
public static class ThemeService
{
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["BgBrush"] = ("#F5F6F8", "#0F1013"),
        ["CardBrush"] = ("#FFFFFF", "#18191D"),
        ["LineBrush"] = ("#ECEEF1", "#24262C"),
        ["TextBrush"] = ("#17191F", "#ECEDF0"),
        ["MutedBrush"] = ("#6A7080", "#9AA0AC"),
        ["AccentBrush"] = ("#3451D1", "#8EA2FF"),
        ["SubtleBrush"] = ("#EEF0F4", "#202228"),
        ["DangerBrush"] = ("#D14343", "#F08585"),
        ["WarningBrush"] = ("#B7791F", "#F0B95A"),
        ["SuccessBrush"] = ("#2E8B62", "#5CCB98"),
        ["SegSelectedBrush"] = ("#FFFFFF", "#2B2E36"),
        ["OnAccentBrush"] = ("#FFFFFF", "#0F1013"), // 青く塗った上の文字
        // グラフの棒。カードの色との差が 3:1 以上になるようにしている
        ["ChartBarBrush"] = ("#3451D1", "#7F95FA"),
        // 左メニュー。窓の背景と同じ色にして、境目の線は見せない。選んでいる項目はカードと同じ色で浮かせる
        ["SidebarBrush"] = ("#F5F6F8", "#0F1013"),
        ["SidebarLineBrush"] = ("#F5F6F8", "#0F1013"), // 右端の線
        ["SidebarTextBrush"] = ("#3C4150", "#C9CDD6"),
        ["SidebarMutedBrush"] = ("#8A90A0", "#7D8391"),
        ["SidebarStrongBrush"] = ("#17191F", "#FFFFFF"), // 選んでいる項目・タイマーの数字
        ["SidebarActiveBrush"] = ("#FFFFFF", "#1C1E23"),
        ["SidebarHoverBrush"] = ("#ECEEF2", "#16171B"),
        // 月のロゴと、選んでいる項目の印
        ["MoonBrush"] = ("#3451D1", "#8EA2FF"),
    };

    public static bool IsDark { get; private set; }

    /// <param name="mode">settings の appearance_mode（LIGHT / DARK / SYSTEM）</param>
    public static void Apply(string mode)
    {
        IsDark = mode switch
        {
            "DARK" => true,
            "SYSTEM" => SystemUsesDarkTheme(),
            _ => false,
        };

        var theme = IsDark ? ApplicationTheme.Dark : ApplicationTheme.Light;
        ApplicationThemeManager.Apply(theme, WindowBackdropType.None, updateAccent: false);
        // 青いボタンなどの色を、Windows の色ではなく霽月の藍色にする
        ApplicationAccentColorManager.Apply((Color)ColorConverter.ConvertFromString(IsDark ? "#6F86F5" : "#3451D1"), theme, false, false);

        var resources = Application.Current.Resources;
        foreach (var (key, (light, dark)) in Palette)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? dark : light));
            brush.Freeze();
            resources[key] = brush;
        }
    }

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    // 付箋の色。id ごとに順番に割り当てる（v6 と同じ 4 色）
    // 付箋の色。最初の 4 色は v6 と同じで、色を選んでいない付箋は id ごとにこの 4 色を順番に使う
    private static readonly (string Name, string Light, string Dark)[] StickyColors =
    [
        ("黄", "#FFF4C7", "#4A4326"),
        ("青", "#E0EEFF", "#253A5A"),
        ("桃", "#FFE3EC", "#4A2836"),
        ("緑", "#E2F5E8", "#24443A"),
        ("紫", "#EDE4FF", "#3A2E55"), // v7 で追加
        ("橙", "#FFE9D6", "#4A3424"), // v7 で追加
    ];

    private const int AutoColorCount = 4;

    public static int StickyColorCount => StickyColors.Length;

    public static string StickyColorName(int index) => StickyColors[index].Name;

    /// <summary>付箋の色の番号。選んでいなければ、id から v6 と同じ色を決める。</summary>
    public static int StickyColorOf(StickyNote note) =>
        note.Color is int color && color >= 0 && color < StickyColors.Length ? color : (int)(note.Id % AutoColorCount);

    public static Brush StickyBrush(StickyNote note) => StickyBrushAt(StickyColorOf(note));

    public static Brush StickyBrushAt(int index)
    {
        var (_, light, dark) = StickyColors[index];
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(IsDark ? dark : light));
        brush.Freeze();
        return brush;
    }

    private static bool SystemUsesDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
}
