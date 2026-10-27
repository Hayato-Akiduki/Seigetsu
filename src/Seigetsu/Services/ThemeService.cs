using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Seigetsu.Services;

/// <summary>明るい／暗い外観の切り替え。色は v6 と同じ (明るい, 暗い) の組。</summary>
public static class ThemeService
{
    private static readonly Dictionary<string, (string Light, string Dark)> Palette = new()
    {
        ["BgBrush"] = ("#EEF2F8", "#0E1524"),
        ["CardBrush"] = ("#FFFFFF", "#172238"),
        ["LineBrush"] = ("#DCE3EE", "#29374F"),
        ["TextBrush"] = ("#1B2840", "#E6ECF6"),
        ["MutedBrush"] = ("#5D6B82", "#9CAAC0"),
        ["AccentBrush"] = ("#3B66C4", "#86AAF5"),
        ["SubtleBrush"] = ("#E8EEF8", "#22304B"),
        ["DangerBrush"] = ("#C94A4A", "#F08585"),
        ["WarningBrush"] = ("#B7791F", "#F0B95A"),
        ["SuccessBrush"] = ("#2E8B62", "#5CCB98"),
        ["SegSelectedBrush"] = ("#FFFFFF", "#3A4E77"),
        ["OnAccentBrush"] = ("#FFFFFF", "#0E1524"), // 青く塗った上の文字
        // グラフの棒。カードの色との差が 3:1 以上になるよう、暗い外観では少し落ち着いた青にしている
        ["ChartBarBrush"] = ("#3B66C4", "#5F86DA"),
        // 左メニュー。明るい外観では白、暗い外観では紺
        ["SidebarBrush"] = ("#FFFFFF", "#18233D"),
        ["SidebarLineBrush"] = ("#DCE3EE", "#18233D"), // 右端の線（暗い外観では見えなくする）
        ["SidebarTextBrush"] = ("#3A4760", "#D3DDEE"),
        ["SidebarMutedBrush"] = ("#5D6B82", "#8D9BB6"),
        ["SidebarStrongBrush"] = ("#1B2840", "#FFFFFF"), // 選んでいる項目・タイマーの数字
        ["SidebarActiveBrush"] = ("#E3EBFA", "#2C3E66"),
        ["SidebarHoverBrush"] = ("#EEF2F8", "#223152"),
        // 月の色。明るい外観の白い地では薄い黄色が見えないので、濃い金色にする
        ["MoonBrush"] = ("#A8741A", "#F3E7B8"),
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
        ApplicationAccentColorManager.Apply((Color)ColorConverter.ConvertFromString("#3B66C4"), theme, false, false);

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
    private static readonly (string Light, string Dark)[] StickyColors =
    [
        ("#FFF4C7", "#4A4326"), // 黄
        ("#E0EEFF", "#253A5A"), // 青
        ("#FFE3EC", "#4A2836"), // 桃
        ("#E2F5E8", "#24443A"), // 緑
    ];

    public static Brush StickyBrush(long id)
    {
        var (light, dark) = StickyColors[(int)(id % StickyColors.Length)];
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
