using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Seigetsu.Data;
using Seigetsu.Services;
using Wpf.Ui.Controls;

namespace Seigetsu.Pages;

/// <summary>タスク一覧の 1 行分の表示用データ。</summary>
public sealed record TaskRowView(
    TaskItem Task,
    Brush TitleBrush,
    TextDecorationCollection? TitleDecorations,
    string PriorityText,
    Brush PriorityBrush,
    string CategoryText,
    string DueText,
    Brush DueBrush,
    Brush FavoriteBrush);

public partial class TasksPage : UserControl, IRefreshable
{
    // 一覧の上の切り替えボタンと同じ順番
    private static readonly TaskFilter[] Filters =
        [TaskFilter.All, TaskFilter.Open, TaskFilter.Done, TaskFilter.Favorite, TaskFilter.Overdue];

    // 文字を打つたびに検索し直さないよう、手が止まってから検索する
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public TasksPage()
    {
        InitializeComponent();
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            Refresh();
        };
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    public void Refresh()
    {
        var db = App.Database;
        var (open, total) = db.TaskCounts();
        SubtitleText.Text = $"未完了 {open}件 ／ 全 {total}件";

        var filter = Filters[Math.Max(FilterList.SelectedIndex, 0)];
        var rows = db.GetTasks(SearchBox.Text, filter).Select(ToRow).ToList();
        TaskList.ItemsSource = rows;

        EmptyText.Text = total == 0
            ? "タスクはまだありません。\n右上の「新しいタスク」から追加できます。"
            : "該当するタスクはありません。";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TaskRowView ToRow(TaskItem task)
    {
        var muted = ThemeService.Brush("MutedBrush");
        // 完了したタスクは「期限切れ」と出さず、締切日だけを出す（v6 と同じ）
        var due = task.Completed && task.DueDate != "" ? task.DueDate : TaskFormat.DueLabel(task.DueDate);
        return new TaskRowView(
            task,
            task.Completed ? muted : ThemeService.Brush("TextBrush"),
            task.Completed ? TextDecorations.Strikethrough : null,
            $"● 優先度 {task.Priority}",
            TaskFormat.PriorityBrush(task.Priority),
            $"#{task.Category}",
            due,
            TaskFormat.DueBrush(task.DueDate, task.Completed),
            task.Favorite ? ThemeService.Brush("WarningBrush") : muted);
    }

    private static TaskItem TaskOf(object sender) => ((TaskRowView)((FrameworkElement)sender).DataContext).Task;

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        // 画面を作っている途中にも呼ばれるので、表示されてからだけ動かす
        if (IsLoaded)
            Refresh();
    }

    private void OnAddClick(object sender, RoutedEventArgs e) => Main.OpenTaskDialog();

    private void OnEditClick(object sender, RoutedEventArgs e) => Main.OpenTaskDialog(TaskOf(sender));

    private void OnCompletedClick(object sender, RoutedEventArgs e)
    {
        var task = TaskOf(sender);
        App.Database.SetTaskCompleted(task.Id, !task.Completed);
        if (!task.Completed)
            Main.Notify($"「{task.Title}」を完了にしました。");
        Main.DataChanged();
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        App.Database.ToggleFavorite(TaskOf(sender).Id);
        Main.DataChanged();
    }

    private async void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        var task = TaskOf(sender);
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Owner = Main,
            Title = "削除の確認",
            Content = $"「{task.Title}」を削除しますか？\nこの操作は元に戻せません。",
            PrimaryButtonText = "削除",
            PrimaryButtonAppearance = ControlAppearance.Danger,
            CloseButtonText = "キャンセル",
            FontFamily = (FontFamily)Application.Current.Resources["AppFont"],
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;
        App.Database.DeleteTask(task.Id);
        Main.Notify("タスクを削除しました。");
        // 確認画面を出している間に別の画面へ移っていることもあるので、表示中の画面を更新する
        Main.DataChanged();
    }
}
