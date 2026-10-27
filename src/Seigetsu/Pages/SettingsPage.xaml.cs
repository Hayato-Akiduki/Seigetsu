using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Seigetsu.Data;

namespace Seigetsu.Pages;

public partial class SettingsPage : UserControl, IRefreshable
{
    // 選べる長さ（v6 と同じ）
    private static readonly int[] WorkChoices = [15, 20, 25, 30, 45, 50, 60, 90];
    private static readonly int[] BreakChoices = [3, 5, 10, 15, 20, 30];

    private bool _loading;

    public SettingsPage()
    {
        InitializeComponent();
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        App.Updates.Changed += UpdateUpdateSection;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => App.Updates.Changed -= UpdateUpdateSection;

    private void UpdateUpdateSection()
    {
        var updates = App.Updates;
        UpdateStatusText.Text = updates.Status;
        CheckUpdateButton.IsEnabled = updates.CanUpdate && updates.ReadyVersion is null;
        UpdateNowButton.Visibility = updates.ReadyVersion is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnCheckUpdateClick(object sender, RoutedEventArgs e) => await App.Updates.CheckAsync(userAsked: true);

    private void OnUpdateNowClick(object sender, RoutedEventArgs e) => Main.UpdateNow();

    public void Refresh()
    {
        var db = App.Database;
        _loading = true;

        var mode = db.GetSetting(SettingKeys.Appearance, "LIGHT");
        AppearanceList.SelectedItem = AppearanceList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == mode)
                                      ?? AppearanceList.Items[0];

        var closeAction = db.GetSetting(SettingKeys.CloseAction, "quit");
        CloseActionList.SelectedItem = CloseActionList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == closeAction)
                                       ?? CloseActionList.Items[0];

        FillMinutes(WorkBox, WorkChoices, db.GetIntSetting(SettingKeys.WorkMinutes, 25));
        FillMinutes(BreakBox, BreakChoices, db.GetIntSetting(SettingKeys.BreakMinutes, 5));

        DataPathText.Text = $"保存場所：{AppPaths.DataDir}";
        var backup = db.BackupPath;
        BackupText.Visibility = File.Exists(backup) ? Visibility.Visible : Visibility.Collapsed;
        BackupText.Text = $"v7 を使う前のデータのコピー：{backup}";

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"霽月 - Seigetsu　バージョン {version?.ToString(3)}（C# 版・開発中）";
        UpdateUpdateSection();
        _loading = false;
    }

    // 一覧に無い長さ（v6 で別の値にしていた、など）も選ばれた状態で出す
    private static void FillMinutes(ComboBox box, int[] choices, int current)
    {
        box.ItemsSource = choices.Append(current).Distinct().Order().ToList();
        box.SelectedItem = current;
    }

    private void OnAppearanceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || AppearanceList.SelectedItem is not ListBoxItem item)
            return;
        var mode = (string)item.Tag;
        App.Database.SetSetting(SettingKeys.Appearance, mode);
        Main.ApplyAppearance(mode);
    }

    private void OnCloseActionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || CloseActionList.SelectedItem is not ListBoxItem item)
            return;
        App.Database.SetSetting(SettingKeys.CloseAction, (string)item.Tag);
    }

    private void OnWorkChanged(object sender, SelectionChangedEventArgs e) =>
        SaveMinutes(SettingKeys.WorkMinutes, WorkBox);

    private void OnBreakChanged(object sender, SelectionChangedEventArgs e) =>
        SaveMinutes(SettingKeys.BreakMinutes, BreakBox);

    private void SaveMinutes(string key, ComboBox box)
    {
        if (_loading || !IsLoaded || box.SelectedItem is not int minutes)
            return;
        App.Database.SetSetting(key, minutes.ToString());
        Main.TimerChanged(); // 左下の小さなタイマーの表示（「次は作業（25分）」など）を合わせる
    }

    private void OnExportClick(object sender, RoutedEventArgs e) => Main.ExportCsv();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppPaths.DataDir);
        Process.Start(new ProcessStartInfo { FileName = AppPaths.DataDir, UseShellExecute = true });
    }
}
