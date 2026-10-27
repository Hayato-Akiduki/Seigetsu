using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Seigetsu.Data;
using Seigetsu.Services;

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
        CityBox.Text = WeatherService.City;

        var dueNotify = db.GetSetting(SettingKeys.DueNotify, "1");
        DueNotifyList.SelectedItem = DueNotifyList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == dueNotify)
                                     ?? DueNotifyList.Items[0];

        var latest = DailyBackup.Latest;
        DailyBackupText.Text = $"毎日の自動バックアップ：{DailyBackup.Folder}（新しい方から {DailyBackup.Keep} 日分を残します。"
                               + (latest is { } day ? $"いちばん新しいもの：{day:yyyy年M月d日}）" : "まだありません）");

        var background = BackgroundImage.CurrentPath;
        BackgroundText.Text = background is null ? "設定なし" : Path.GetFileName(background);
        BackgroundText.ToolTip = background;
        ClearBackgroundButton.IsEnabled = background is not null;

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

    private void OnDueNotifyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || DueNotifyList.SelectedItem is not ListBoxItem item)
            return;
        App.Database.SetSetting(SettingKeys.DueNotify, (string)item.Tag);
    }

    private void OnChooseBackgroundClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "背景画像を選ぶ", Filter = BackgroundImage.FileFilter };
        if (dialog.ShowDialog(Main) != true)
            return;
        // 読めない画像は設定しない（v6 と同じ）
        if (BackgroundImage.Load(dialog.FileName) is null)
        {
            Main.Notify("この画像は読み込めませんでした。ほかの画像を選んでください。", Wpf.Ui.Controls.ControlAppearance.Danger, 5);
            return;
        }
        App.Database.SetSetting(SettingKeys.Background, dialog.FileName);
        Main.Notify("ホームの背景画像を設定しました。");
        Refresh();
    }

    private void OnClearBackgroundClick(object sender, RoutedEventArgs e)
    {
        App.Database.SetSetting(SettingKeys.Background, "");
        Main.Notify("ホームの背景画像を解除しました。");
        Refresh();
    }

    private void OnCityKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
            SaveCity();
    }

    private void OnSaveCityClick(object sender, RoutedEventArgs e) => SaveCity();

    /// <summary>地域を保存して、すぐ天気を取り直す（v6 と同じ）。空なら最初の地域に戻す。</summary>
    private async void SaveCity()
    {
        var city = string.IsNullOrWhiteSpace(CityBox.Text) ? WeatherService.DefaultCity : CityBox.Text.Trim();
        CityBox.Text = city;
        App.Database.SetSetting(SettingKeys.City, city);
        Main.Notify($"天気の地域を「{city}」にしました。");
        await App.Weather.RefreshAsync();
        if (App.Weather.Status == WeatherStatus.Error)
            Main.Notify(App.Weather.Message, Wpf.Ui.Controls.ControlAppearance.Danger, 6);
    }

    private void OnExportClick(object sender, RoutedEventArgs e) => Main.ExportCsv();

    private void OnOpenFolderClick(object sender, RoutedEventArgs e) => OpenFolder(AppPaths.DataDir);

    private void OnOpenBackupFolderClick(object sender, RoutedEventArgs e) => OpenFolder(DailyBackup.Folder);

    private void OnOpenScreenshotFolderClick(object sender, RoutedEventArgs e) => OpenFolder(Screenshot.Folder);

    private static void OpenFolder(string folder)
    {
        Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }
}
