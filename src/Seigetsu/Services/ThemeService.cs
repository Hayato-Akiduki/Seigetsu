using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Seigetsu.Data;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Seigetsu.Services;

/// <summary>明るい／暗い外観の切り替え。明るい外観は白と青、暗い外観は黒と青。色は (明るい, 暗い) の組。</summary>
public static class ThemeService
{
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["BgBrush"] = ("#F3F6FC", "#0A0A0C"),
        ["CardBrush"] = ("#FFFFFF", "#141418"),
        ["LineBrush"] = ("#DDE5F2", "#27272E"),
        ["TextBrush"] = ("#14213D", "#EDEFF3"),
        ["MutedBrush"] = ("#5B6B86", "#A0A6B2"),
        ["AccentBrush"] = ("#2563EB", "#5B9BFF"),
        ["SubtleBrush"] = ("#EAF1FD", "#1C1C22"),
        ["DangerBrush"] = ("#C94A4A", "#F08585"),
        ["WarningBrush"] = ("#B7791F", "#F0B95A"),
        ["SuccessBrush"] = ("#2E8B62", "#5CCB98"),
        ["SegSelectedBrush"] = ("#FFFFFF", "#1E2A44"),
        ["OnAccentBrush"] = ("#FFFFFF", "#0A0A0C"), // 青く塗った上の文字
        // グラフの棒。カードの色との差が 3:1 以上になるようにしている
        ["ChartBarBrush"] = ("#2563EB", "#4F8DF5"),
        // 左メニュー。明るい外観では白、暗い外観では黒。選んでいる項目は青
        ["SidebarBrush"] = ("#FFFFFF", "#000000"),
        ["SidebarLineBrush"] = ("#DDE5F2", "#1F1F25"), // 右端の線
        ["SidebarTextBrush"] = ("#33415C", "#D5D9E2"),
        ["SidebarMutedBrush"] = ("#5B6B86", "#8E95A3"),
        ["SidebarStrongBrush"] = ("#1D4ED8", "#FFFFFF"), // 選んでいる項目・タイマーの数字
        ["SidebarActiveBrush"] = ("#E6EEFD", "#16233F"),
        ["SidebarHoverBrush"] = ("#F1F5FC", "#15151A"),
        // 月のロゴと、選んでいる項目の印
        ["MoonBrush"] = ("#2563EB", "#5B9BFF"),
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
        // 青いボタンなどの色を、Windows の色ではなく霽月の青にする
        ApplicationAccentColorManager.Apply((Color)ColorConverter.ConvertFromString("#2563EB"), theme, false, false);

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
