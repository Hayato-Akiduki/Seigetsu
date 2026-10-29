using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using Seigetsu.Data;
using Seigetsu.Services;

namespace Seigetsu.Pages;

/// <summary>右側の「選んだ日のタスク」1 行分。</summary>
public sealed record DayTaskView(
    TaskItem Task, Brush TitleBrush, TextDecorationCollection? TitleDecorations, string PriorityText, Brush PriorityBrush);

/// <summary>右側の「授業」1 行分（長崎大学モード）。</summary>
public sealed record DayClassView(ClassSlot Slot, string PeriodText, string TimeText, Visibility DetailVisibility);

public partial class CalendarPage : UserControl, IRefreshable
{
    private static readonly string[] WeekNames = ["日", "月", "火", "水", "木", "金", "土"];
    private static readonly CultureInfo Japanese = new("ja-JP");

    private readonly Button[] _cells = new Button[42];
    private readonly DateTime[] _cellDates = new DateTime[42];

    // 表示中の月と選んだ日。ほかの画面から戻っても同じ月のまま
    private int _year = DateTime.Today.Year;
    private int _month = DateTime.Today.Month;
    private DateTime _selected = DateTime.Today;

    public CalendarPage()
    {
        InitializeComponent();
        for (var c = 0; c < 7; c++)
        {
            WeekHeader.Children.Add(new TextBlock
            {
                Text = WeekNames[c],
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
        }
        for (var i = 0; i < 42; i++)
        {
            var index = i;
            var cell = new Button { Style = (Style)Resources["DayCell"] };
            cell.Click += (_, _) => SelectCell(index);
            _cells[i] = cell;
            DayGrid.Children.Add(cell);
        }
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private static string Iso(DateTime day) => day.ToString("yyyy-MM-dd");

    // 日曜は赤、土曜は青（v6 と同じ）
    private static Brush WeekdayBrush(int column) => ThemeService.Brush(column switch
    {
        0 => "DangerBrush",
        6 => "AccentBrush",
        _ => "MutedBrush",
    });

    private static string DateLabel(DateTime day) => day.ToString("yyyy年M月d日（ddd）", Japanese);

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    // ---- 操作 ---------------------------------------------------------
    private void Move(int months)
    {
        var first = new DateTime(_year, _month, 1).AddMonths(months);
        (_year, _month) = (first.Year, first.Month);
        Refresh();
    }

    private void OnPrevClick(object sender, RoutedEventArgs e) => Move(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => Move(1);

    private void OnTodayClick(object sender, RoutedEventArgs e)
    {
        _selected = DateTime.Today;
        (_year, _month) = (_selected.Year, _selected.Month);
        Refresh();
    }

    private void SelectCell(int index)
    {
        _selected = _cellDates[index];
        // 前後の月の日を押したら、その月へ移る（v6 と同じ）
        (_year, _month) = (_selected.Year, _selected.Month);
        Refresh();
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => Main.OpenTaskDialog(presetDue: Iso(_selected));

    private void OnNagasakiClick(object sender, RoutedEventArgs e) =>
        Main.UpdateNagasakiNow();

    private static TaskItem TaskOf(object sender) => ((DayTaskView)((FrameworkElement)sender).DataContext).Task;

    private void OnCompletedClick(object sender, RoutedEventArgs e)
    {
        var task = TaskOf(sender);
        App.Database.SetTaskCompleted(task.Id, !task.Completed);
        Main.DataChanged();
    }

    private void OnEditClick(object sender, RoutedEventArgs e) => Main.OpenTaskDialog(TaskOf(sender));

    // ---- 表示 ---------------------------------------------------------
    public void Refresh()
    {
        MonthText.Text = $"{_year}年 {_month}月";

        // 曜日の色は外観で変わるので、表示のたびに塗り直す
        for (var c = 0; c < 7; c++)
            ((TextBlock)WeekHeader.Children[c]).Foreground = WeekdayBrush(c);

        // 日曜始まりで 6 週分（42 日）並べる
        var first = new DateTime(_year, _month, 1);
        var start = first.AddDays(-(int)first.DayOfWeek);
        for (var i = 0; i < 42; i++)
            _cellDates[i] = start.AddDays(i);

        var counts = App.Database.TaskCountsBetween(Iso(_cellDates[0]), Iso(_cellDates[41]));
        var today = DateTime.Today;
        for (var i = 0; i < 42; i++)
        {
            var day = _cellDates[i];
            counts.TryGetValue(Iso(day), out var count);
            UpdateCell(_cells[i], day, i % 7, count.Open, count.Done, day == today);
        }
        RefreshDay();
    }

    private void UpdateCell(Button cell, DateTime day, int column, int open, int done, bool isToday)
    {
        var selected = day == _selected;
        var inMonth = day.Month == _month;

        // 未完了があれば ●（2 件以上なら件数も）、全部終わっていれば ✓
        var mark = open > 1 ? $"● {open}" : open == 1 ? "●" : done > 0 ? "✓" : " ";

        // 選んだ日は青く塗る。文字は、明るい外観では白、暗い外観では濃い紺（v6 と同じ）
        var onAccent = ThemeService.Brush("OnAccentBrush");
        Brush foreground;
        if (selected)
            foreground = onAccent;
        else if (!inMonth)
            foreground = ThemeService.Brush("LineBrush");
        else if (column is 0 or 6)
            foreground = WeekdayBrush(column);
        else
            foreground = ThemeService.Brush("TextBrush");

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = day.Day.ToString(),
            FontSize = 14,
            FontWeight = isToday ? FontWeights.Bold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = foreground,
        });
        panel.Children.Add(new TextBlock
        {
            Text = mark,
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = selected ? onAccent
                : open > 0 ? ThemeService.Brush(inMonth ? "AccentBrush" : "LineBrush")
                : ThemeService.Brush(inMonth ? "SuccessBrush" : "LineBrush"),
        });
        cell.Content = panel;
        cell.Tag = selected ? "selected" : "normal";
        cell.Background = selected ? ThemeService.Brush("AccentBrush") : Brushes.Transparent;
        // 今日は青い枠で囲む（選んでいるときは塗りつぶしで十分なので枠なし）
        cell.BorderBrush = isToday && !selected ? ThemeService.Brush("AccentBrush") : Brushes.Transparent;

        // 読み上げソフト向けの名前（例: 2026年10月27日（火） 未完了 2件）
        var summary = open > 0 ? $" 未完了 {open}件" : done > 0 ? " すべて完了" : "";
        AutomationProperties.SetName(cell, DateLabel(day) + summary);
    }

    private void RefreshDay()
    {
        DayTitle.Text = DateLabel(_selected);

        // 長崎大学モード: その曜日の授業（日曜は無し）
        var nagasaki = NagasakiUniversity.Enabled;
        NagasakiButton.Visibility = nagasaki ? Visibility.Visible : Visibility.Collapsed;
        var classes = nagasaki && _selected.DayOfWeek != DayOfWeek.Sunday
            ? App.Database.GetTimetable((int)_selected.DayOfWeek)
            : [];
        ClassesPanel.Visibility = classes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DayClasses.ItemsSource = classes.Select(s => new DayClassView(
            s, $"{s.Period}限", NagasakiUniversity.TimeOf(s),
            s.Detail == "" ? Visibility.Collapsed : Visibility.Visible)).ToList();

        var tasks = App.Database.TasksOn(Iso(_selected));
        var openCount = tasks.Count(t => !t.Completed);
        DaySub.Text = tasks.Count > 0 ? $"締切のタスク {tasks.Count}件（未完了 {openCount}件）" : "";
        NoTasksText.Visibility = tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var muted = ThemeService.Brush("MutedBrush");
        DayTasks.ItemsSource = tasks.Select(t => new DayTaskView(
            t,
            t.Completed ? muted : ThemeService.Brush("TextBrush"),
            t.Completed ? TextDecorations.Strikethrough : null,
            $"● {t.Priority}",
            TaskFormat.PriorityBrush(t.Priority))).ToList();
    }
}
