using System.Windows;
using System.Windows.Controls;
using Seigetsu.Data;
using Wpf.Ui.Controls;

namespace Seigetsu.Dialogs;

/// <summary>タスクの追加と編集を兼ねる入力画面。保存したら ShowDialog() が true を返す。</summary>
public partial class TaskDialog : FluentWindow
{
    private static readonly string[] Priorities = ["高", "中", "低"];

    public TaskDialog(TaskItem? task, IEnumerable<string> categories, string presetDue = "")
    {
        InitializeComponent();
        var editing = task is not null;
        Title = editing ? "タスクを編集" : "新しいタスク";
        HeadingText.Text = Title;
        SaveButton.Content = editing ? "保存" : "追加";

        CategoryBox.ItemsSource = new[] { "一般" }.Concat(categories).Distinct().ToList();

        // 知らない値が入っていたら「中」にする
        var index = Array.IndexOf(Priorities, task?.Priority ?? "中");
        PriorityList.SelectedIndex = index >= 0 ? index : 1;

        TitleBox.Text = task?.Title ?? "";
        CategoryBox.Text = string.IsNullOrEmpty(task?.Category) ? "一般" : task.Category;
        SetDue(task?.DueDate ?? presetDue);
    }

    // 保存された内容（呼び出し側が読む）
    public string ResultTitle { get; private set; } = "";
    public string ResultPriority { get; private set; } = "中";
    public string ResultDue { get; private set; } = "";
    public string ResultCategory { get; private set; } = "一般";

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        TitleBox.Focus();
        TitleBox.SelectAll();
    }

    private void SetDue(string due)
    {
        DuePicker.SelectedDate = DateTime.TryParseExact(due, "yyyy-MM-dd", null,
            System.Globalization.DateTimeStyles.None, out var date) ? date : null;
    }

    private void OnQuickDueClick(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        DuePicker.SelectedDate = tag == "" ? null : DateTime.Today.AddDays(int.Parse(tag));
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var title = TitleBox.Text.Trim();
        if (title == "")
        {
            ErrorText.Text = "タスク名を入力してください。";
            TitleBox.Focus();
            return;
        }
        // 日付として読めない文字が入っているとき
        if (DuePicker.SelectedDate is null && !string.IsNullOrWhiteSpace(DuePicker.Text))
        {
            ErrorText.Text = "締切日は 2026/08/05 のように入力してください。";
            DuePicker.Focus();
            return;
        }

        ResultTitle = title;
        ResultPriority = (string)((ListBoxItem)PriorityList.SelectedItem).Content;
        ResultDue = DuePicker.SelectedDate?.ToString("yyyy-MM-dd") ?? "";
        ResultCategory = string.IsNullOrWhiteSpace(CategoryBox.Text) ? "一般" : CategoryBox.Text.Trim();
        DialogResult = true;
    }
}
