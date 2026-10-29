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
        App.Nagasaki.Changed += OnNagasakiChanged;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        App.Updates.Changed -= UpdateUpdateSection;
        App.Nagasaki.Changed -= OnNagasakiChanged;
    }

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

        // 自動起動はデータベースではなく Windows 側に入っているので、そちらを見る
        AutoStartList.SelectedIndex = AutoStart.IsEnabled ? 0 : 1;

        FillMinutes(WorkBox, WorkChoices, db.GetIntSetting(SettingKeys.WorkMinutes, 25));
        FillMinutes(BreakBox, BreakChoices, db.GetIntSetting(SettingKeys.BreakMinutes, 5));
        CityBox.Text = WeatherService.City;

        var dueNotify = db.GetSetting(SettingKeys.DueNotify, "1");
        DueNotifyList.SelectedItem = DueNotifyList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == dueNotify)
                                     ?? DueNotifyList.Items[0];

        var nagasaki = db.GetSetting(SettingKeys.NagasakiMode, "0");
        NagasakiModeList.SelectedItem = NagasakiModeList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == nagasaki)
                                        ?? NagasakiModeList.Items[1];
        NagasakiPanel.Visibility = nagasaki == "1" ? Visibility.Visible : Visibility.Collapsed;
        // 「追加の機能」は、ふだんは畳んでおく。長崎大学モードを使っている人には開いて見せる
        if (nagasaki == "1")
            ExtrasExpander.IsExpanded = true;
        var classes = db.GetTimetable().Count;
        NagasakiSyncButton.IsEnabled = !App.Nagasaki.IsRunning;
        NagasakiSyncButton.Content = App.Nagasaki.IsRunning ? "更新しています…" : "今すぐ更新";
        NagasakiStatusText.Text = "前回の更新：" + NagasakiSync.LastStatus;
        ClearTimetableButton.IsEnabled = classes > 0;

        var latest = DailyBackup.Latest;
        DailyBackupText.Text = $"毎日の自動バックアップ：{DailyBackup.Folder}（新しい方から {DailyBackup.Keep} 日分を残します。"
                               + (latest is { } day ? $"いちばん新しいもの：{day:yyyy年M月d日}）" : "まだありません）");

        var background = BackgroundImage.CurrentPath;
        BackgroundText.Text = background is null ? "設定なし" : Path.GetFileName(background);
        BackgroundText.ToolTip = background;
        ClearBackgroundButton.IsEnabled = background is not null;
        BackgroundOptions.IsEnabled = background is not null;
        BackgroundBannerCheck.IsChecked = BackgroundImage.IsShownIn(BackgroundPlace.Banner);
        BackgroundWindowCheck.IsChecked = BackgroundImage.IsShownIn(BackgroundPlace.Window);
        BackgroundClockCheck.IsChecked = BackgroundImage.IsShownIn(BackgroundPlace.Clock);
        var strength = db.GetSetting(SettingKeys.BackgroundStrength, "NORMAL");
        BackgroundStrengthList.SelectedItem = BackgroundStrengthList.Items.Cast<ListBoxItem>()
                                                  .FirstOrDefault(i => (string)i.Tag == strength)
                                              ?? BackgroundStrengthList.Items[1];

        FillBannerKinds();
        var interval = BannerMessages.IntervalSeconds.ToString();
        BannerIntervalList.SelectedItem = BannerIntervalList.Items.Cast<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == interval)
                                          ?? BannerIntervalList.Items[1];
        if (!BannerCustomBox.IsKeyboardFocusWithin)
            BannerCustomBox.Text = db.GetSetting(SettingKeys.BannerCustom, "");

        DataPathText.Text = $"データベースの場所：{db.Path}（v6 と同じデータを使っています）";
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

    private void OnAutoStartChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || AutoStartList.SelectedItem is not ListBoxItem item)
            return;
        var enabled = (string)item.Tag == "1";
        if (AutoStart.SetEnabled(enabled))
        {
            Main.Notify(enabled ? "Windows の起動時に、霽月が自動で起動するようにしました。" : "自動で起動しないようにしました。");
            return;
        }
        Main.Notify("自動起動の設定を変えられませんでした。", Wpf.Ui.Controls.ControlAppearance.Danger, 6);
        _loading = true;
        AutoStartList.SelectedIndex = AutoStart.IsEnabled ? 0 : 1;
        _loading = false;
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

    private void OnNagasakiModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || NagasakiModeList.SelectedItem is not ListBoxItem item)
            return;
        App.Database.SetSetting(SettingKeys.NagasakiMode, (string)item.Tag);
        Refresh();
        // オンにしたらすぐ更新する（初めてでログインがまだなら、ログインの窓が開く）
        if ((string)item.Tag == "1")
            Main.UpdateNagasakiNow();
    }

    private void OnNagasakiSyncClick(object sender, RoutedEventArgs e) => Main.UpdateNagasakiNow();

    private void OnNagasakiLoginClick(object sender, RoutedEventArgs e) => Main.OpenNagasakiLogin();

    // 更新が始まった・終わったときに、ボタンと結果の文を合わせる
    private void OnNagasakiChanged() => Dispatcher.BeginInvoke(Refresh);

    private async void OnClearTimetableClick(object sender, RoutedEventArgs e)
    {
        var result = await new Wpf.Ui.Controls.MessageBox
        {
            Title = "時間割を消す",
            Content = "取り込んだ時間割を消しますか？（タスクは消えません）",
            PrimaryButtonText = "消す",
            CloseButtonText = "やめる",
            Owner = Main,
        }.ShowDialogAsync();
        if (result != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;
        App.Database.ClearTimetable();
        Main.Notify("時間割を消しました。");
        Refresh();
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
        Main.Notify("背景画像を設定しました。");
        Main.ApplyBackground(); // 表示中のこの画面も作り直される
    }

    private void OnClearBackgroundClick(object sender, RoutedEventArgs e)
    {
        App.Database.SetSetting(SettingKeys.Background, "");
        Main.Notify("背景画像を解除しました。");
        Main.ApplyBackground();
    }

    private void OnBackgroundPlaceClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string tag } check || !Enum.TryParse<BackgroundPlace>(tag, out var place))
            return;
        BackgroundImage.SetShownIn(place, check.IsChecked == true);
        Main.ApplyBackground();
    }

    private void OnBackgroundStrengthChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || BackgroundStrengthList.SelectedItem is not ListBoxItem item)
            return;
        App.Database.SetSetting(SettingKeys.BackgroundStrength, (string)item.Tag);
        Main.ApplyBackground();
    }

    // 上帯のことばの種類のチェックを、一覧から作る
    private void FillBannerKinds()
    {
        var enabled = BannerMessages.EnabledKinds;
        BannerKindsPanel.Children.Clear();
        foreach (var (key, label) in BannerMessages.Kinds)
        {
            var check = new CheckBox
            {
                Content = label,
                IsChecked = enabled.Contains(key),
                Margin = new Thickness(0, 0, 20, 4),
            };
            check.Click += (_, _) => BannerMessages.SetEnabled(key, check.IsChecked == true);
            BannerKindsPanel.Children.Add(check);
        }
    }

    private void OnBannerIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded || BannerIntervalList.SelectedItem is not ListBoxItem item)
            return;
        App.Database.SetSetting(SettingKeys.BannerInterval, (string)item.Tag);
    }

    private void OnBannerCustomLostFocus(object sender, RoutedEventArgs e)
    {
        var text = BannerCustomBox.Text.Replace("\r\n", "\n").Trim();
        if (text == App.Database.GetSetting(SettingKeys.BannerCustom, ""))
            return;
        App.Database.SetSetting(SettingKeys.BannerCustom, text);
        Main.Notify("自分で書いたことばを保存しました。");
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
