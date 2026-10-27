using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Seigetsu.Controls;
using Seigetsu.Services;

namespace Seigetsu.Pages;

public partial class StatsPage : UserControl, IRefreshable
{
    private static readonly CultureInfo Japanese = new("ja-JP");

    // 曜日別は月曜始まり。キーは SQLite の strftime('%w')（0 = 日曜）
    private static readonly (string Key, string Name)[] WeekdayOrder =
        [("1", "月"), ("2", "火"), ("3", "水"), ("4", "木"), ("5", "金"), ("6", "土"), ("0", "日")];

    private bool _showTable;

    public StatsPage()
    {
        InitializeComponent();
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private static string Iso(DateTime day) => day.ToString("yyyy-MM-dd");

    /// <summary>棒の上に出す短い書き方（v6 と同じ）: 60 分以上は「1.5h」、それ未満は「45分」。</summary>
    private static string ShortMinutes(double seconds)
    {
        var minutes = seconds / 60;
        return minutes >= 60 ? $"{minutes / 60:0.0}h" : $"{minutes:0}分";
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    public void Refresh()
    {
        var db = App.Database;
        var today = DateTime.Today;
        TodayText.Text = Format.Duration(db.WorkSecondsSince(Iso(today)));
        WeekText.Text = Format.Duration(db.WorkSecondsSince(Iso(today.AddDays(-6))));
        MonthText.Text = Format.Duration(db.WorkSecondsSince(Iso(new DateTime(today.Year, today.Month, 1))));
        AllText.Text = Format.Duration(db.WorkSecondsSince());
        DrawChart();
    }

    private void DrawChart()
    {
        var (title, points, highlight) = ModeList.SelectedIndex switch
        {
            1 => Weekdays(),
            2 => Months(),
            _ => Week(),
        };
        ChartTitle.Text = title;
        Chart.SetData(points, highlight);
        TableRows.ItemsSource = points;
    }

    // 直近 7 日間。今日の棒を強調する
    private static (string, List<ChartPoint>, int?) Week()
    {
        var today = DateTime.Today;
        var totals = App.Database.DailyTotals(Iso(today.AddDays(-6)));
        var points = Enumerable.Range(0, 7).Select(offset =>
        {
            var day = today.AddDays(offset - 6);
            totals.TryGetValue(Iso(day), out var seconds);
            var label = offset == 6 ? "今日" : $"{day.Month}/{day.Day}";
            return new ChartPoint(label, seconds, ShortMinutes(seconds),
                $"{day.ToString("M月d日（ddd）", Japanese)}  {Format.Duration(seconds)}");
        }).ToList();
        var sum = points.Sum(p => p.Value);
        return ($"直近7日間の作業時間（合計 {Format.Duration(sum)}）", points, 6);
    }

    // 曜日別（これまでの累計）
    private static (string, List<ChartPoint>, int?) Weekdays()
    {
        var totals = App.Database.WeekdayTotals();
        var points = WeekdayOrder.Select(w =>
        {
            totals.TryGetValue(w.Key, out var seconds);
            return new ChartPoint(w.Name, seconds, ShortMinutes(seconds), $"{w.Name}曜日  {Format.Duration(seconds)}");
        }).ToList();
        return ("曜日別の作業時間（累計）", points, null);
    }

    // 直近 6 か月。記録のない月も 0 として並べる
    private static (string, List<ChartPoint>, int?) Months()
    {
        var thisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        var first = thisMonth.AddMonths(-5);
        var totals = App.Database.MonthlyTotals(first.ToString("yyyy-MM"));
        var points = Enumerable.Range(0, 6).Select(i =>
        {
            var month = first.AddMonths(i);
            totals.TryGetValue(month.ToString("yyyy-MM"), out var seconds);
            return new ChartPoint($"{month.Month}月", seconds, $"{seconds / 3600.0:0.0}h",
                $"{month.Year}年{month.Month}月  {Format.Duration(seconds)}");
        }).ToList();
        return ("月ごとの作業時間（直近6か月）", points, 5);
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded)
            DrawChart();
    }

    private void OnViewClick(object sender, RoutedEventArgs e)
    {
        _showTable = !_showTable;
        Chart.Visibility = _showTable ? Visibility.Collapsed : Visibility.Visible;
        TableView.Visibility = _showTable ? Visibility.Visible : Visibility.Collapsed;
        ViewButton.Content = _showTable ? "グラフで見る" : "表で見る";
        ViewButton.Icon = new Wpf.Ui.Controls.SymbolIcon(_showTable
            ? Wpf.Ui.Controls.SymbolRegular.DataBarVertical24
            : Wpf.Ui.Controls.SymbolRegular.Table24);
    }

    private void OnExportClick(object sender, RoutedEventArgs e) => Main.ExportCsv();
}
