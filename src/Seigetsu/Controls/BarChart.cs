using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Seigetsu.Services;

namespace Seigetsu.Controls;

/// <summary>棒 1 本分のデータ。ValueText は棒の上に出す短い数字、Detail はマウスを乗せたときの説明。</summary>
public sealed record ChartPoint(string Label, double Value, string ValueText, string Detail);

/// <summary>
/// 1 系列だけの棒グラフ。棒の高さは Grid の比率で決めるので、窓の大きさが変わっても描き直しはいらない。
/// 数字はすべての棒には付けず、強調する棒（今日など）と一番高い棒だけに付ける。
/// ほかの棒の値は、マウスを乗せたときの吹き出しと「表で見る」で読める（読み上げソフトも「表で見る」で読める）。
/// </summary>
public sealed class BarChart : Grid
{
    private const double LabelSpace = 22; // 棒の上の数字を置く場所

    public void SetData(IReadOnlyList<ChartPoint> points, int? highlight = null)
    {
        Children.Clear();
        RowDefinitions.Clear();
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var peak = points.Count > 0 ? points.Max(p => p.Value) : 0;
        if (peak <= 0)
        {
            var empty = new TextBlock
            {
                Text = "まだ記録がありません。\n集中タイマーを使うとここに表示されます。",
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 13,
                Foreground = ThemeService.Brush("MutedBrush"),
            };
            SetRowSpan(empty, 2);
            Children.Add(empty);
            return;
        }
        var peakIndex = points.Select((p, i) => (p.Value, i)).Max().i;

        Children.Add(GridLines());

        var bars = new UniformGrid { Rows = 1, Columns = points.Count };
        var labels = new UniformGrid { Rows = 1, Columns = points.Count, Margin = new Thickness(0, 6, 0, 0) };
        SetRow(labels, 1);
        for (var i = 0; i < points.Count; i++)
        {
            var emphasized = i == highlight;
            bars.Children.Add(BarCell(points[i], peak, showValue: emphasized || i == peakIndex));
            labels.Children.Add(new TextBlock
            {
                Text = points[i].Label,
                FontSize = 11,
                FontWeight = emphasized ? FontWeights.Bold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = ThemeService.Brush(emphasized ? "TextBrush" : "MutedBrush"),
            });
        }
        Children.Add(bars);
        Children.Add(labels);
    }

    // 控えめな目盛り線 3 本と、しっかりした底の線
    private static UIElement GridLines()
    {
        var lines = new Grid { Margin = new Thickness(0, LabelSpace, 0, 0) };
        for (var r = 0; r < 3; r++)
        {
            lines.RowDefinitions.Add(new RowDefinition());
            var line = new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeService.Brush("LineBrush") };
            SetRow(line, r);
            lines.Children.Add(line);
        }
        var baseline = new Border
        {
            Height = 1,
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = ThemeService.Brush("MutedBrush"),
        };
        SetRowSpan(baseline, 3);
        lines.Children.Add(baseline);
        return lines;
    }

    private static UIElement BarCell(ChartPoint point, double peak, bool showValue)
    {
        // 列全体をマウスの当たり判定にする（細い棒だけだと狙いにくいため）
        var cell = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            ToolTip = point.Detail,
        };
        ToolTipService.SetInitialShowDelay(cell, 100);
        cell.MouseEnter += (_, _) => cell.Background = ThemeService.Brush("SubtleBrush");
        cell.MouseLeave += (_, _) => cell.Background = System.Windows.Media.Brushes.Transparent;

        // 上から: 数字の場所 / 空き（peak − 値）/ 棒（値）
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(LabelSpace) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(peak - point.Value, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(point.Value, GridUnitType.Star) });
        // 棒は列の真ん中 56%（最大 56px）
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56, GridUnitType.Star), MaxWidth = 56 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22, GridUnitType.Star) });

        if (point.Value > 0)
        {
            var bar = new Border
            {
                Background = ThemeService.Brush("ChartBarBrush"),
                CornerRadius = new CornerRadius(4, 4, 0, 0), // 上だけ丸く、底は線にそろえる
                MinHeight = 2,
            };
            SetRow(bar, 2);
            SetColumn(bar, 1);
            grid.Children.Add(bar);
        }

        if (showValue && point.Value > 0)
        {
            var value = new TextBlock
            {
                Text = point.ValueText,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = ThemeService.Brush("TextBrush"),
            };
            SetRowSpan(value, 2);
            SetColumnSpan(value, 3);
            grid.Children.Add(value);
        }

        cell.Child = grid;
        return cell;
    }
}
