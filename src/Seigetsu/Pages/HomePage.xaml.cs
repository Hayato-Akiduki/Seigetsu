using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Seigetsu.Data;
using Seigetsu.Services;

namespace Seigetsu.Pages;

/// <summary>ホームのタスク 1 行分の表示用データ。</summary>
public sealed record TaskRow(string Title, string Detail, Brush PriorityBrush, Visibility FavoriteVisibility);

public partial class HomePage : UserControl, IRefreshable
{
    private static readonly CultureInfo Japanese = new("ja-JP");
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };

    public HomePage()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => UpdateClock();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateClock();
        _clock.Start();
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _clock.Stop();

    private void UpdateClock()
    {
        var now = DateTime.Now;
        GreetingText.Text = now.Hour switch
        {
            >= 5 and < 11 => "おはようございます",
            >= 11 and < 18 => "こんにちは",
            _ => "こんばんは",
        };
        DateText.Text = now.ToString("yyyy年M月d日（ddd）", Japanese);
        ClockText.Text = now.ToString("HH:mm");
    }

    public void Refresh()
    {
        var db = App.Database;
        DbPathText.Text = db.Path;

        OpenTasksText.Text = $"{db.TaskCounts().Open} 件";
        TodayWorkText.Text = Format.Duration(db.WorkSecondsToday());
        TotalWorkText.Text = Format.Duration(db.WorkSecondsSince());

        var rows = db.TodayTasks().Select(ToRow).ToList();
        TaskList.ItemsSource = rows;
        NoTasksText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TaskRow ToRow(TaskItem task) => new(
        task.Title,
        $"{task.Category} ・ 優先度 {task.Priority} ・ {TaskFormat.DueLabel(task.DueDate)}",
        TaskFormat.PriorityBrush(task.Priority),
        task.Favorite ? Visibility.Visible : Visibility.Collapsed);
}
