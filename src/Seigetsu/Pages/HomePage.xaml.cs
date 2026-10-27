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
        App.Weather.Changed += UpdateWeather;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _clock.Stop();
        App.Weather.Changed -= UpdateWeather;
    }

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private void UpdateWeather()
    {
        var weather = App.Weather;
        WeatherRefreshButton.IsEnabled = weather.Status != WeatherStatus.Loading;
        switch (weather.Status)
        {
            case WeatherStatus.Loading:
                WeatherDetail.Text = "確認しています…";
                break;
            case WeatherStatus.Error:
                WeatherIcon.Symbol = Wpf.Ui.Controls.SymbolRegular.Warning24;
                WeatherTitle.Text = "天気を取得できませんでした";
                WeatherDetail.Text = weather.Message;
                break;
            case WeatherStatus.Ok when weather.Current is { } w:
                WeatherIcon.Symbol = w.Icon;
                WeatherTitle.Text = $"{w.Place}　{w.Text}";
                WeatherDetail.Text = $"気温 {Number(w.Temperature)}℃ ・ 風速 {Number(w.Wind)} km/h";
                break;
        }
    }

    private static string Number(double? value) => value?.ToString("0.#", CultureInfo.InvariantCulture) ?? "―";

    private async void OnWeatherRefreshClick(object sender, RoutedEventArgs e) => await App.Weather.RefreshAsync();

    private void OnCalculatorClick(object sender, RoutedEventArgs e) => Main.OpenCalculator();

    private void OnCounterClick(object sender, RoutedEventArgs e) => Main.OpenCharCounter();

    private void OnScreenshotClick(object sender, RoutedEventArgs e) => Main.TakeScreenshot();

    private void OnNewStickyClick(object sender, RoutedEventArgs e) => Main.OpenStickyDialog();

    private void OnBigClockClick(object sender, RoutedEventArgs e) => Main.ShowBigClock();

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

        UpdateWeather();
        UpdateBanner();

        var rows = db.TodayTasks().Select(ToRow).ToList();
        TaskList.ItemsSource = rows;
        NoTasksText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private string? _bannerPath;

    /// <summary>背景画像を選んでいれば見出しに敷く。選んでいなければ、ふつうの見出しに戻す。</summary>
    private void UpdateBanner()
    {
        var path = BackgroundImage.CurrentPath;
        // 同じ画像なら読み直さない（データが変わるたびに呼ばれるので）
        if (path != _bannerPath || (path is not null && Banner.Background is null))
        {
            _bannerPath = path;
            var image = path is null ? null : BackgroundImage.Load(path);
            Banner.Background = image is null ? null : new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        }

        var hasImage = Banner.Background is not null;
        BannerShade.Visibility = hasImage ? Visibility.Visible : Visibility.Collapsed;
        BannerContent.Margin = hasImage ? new Thickness(30, 30, 24, 30) : new Thickness(0);
        if (hasImage)
        {
            // 暗くした画像の上なので、外観にかかわらず白い文字
            GreetingText.Foreground = Brushes.White;
            ClockText.Foreground = Brushes.White;
            DateText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xE6, 0xF7));
            BigClockIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xE6, 0xF7));
        }
        else
        {
            GreetingText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            ClockText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            DateText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            BigClockIcon.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "MutedBrush");
        }
    }

    private static TaskRow ToRow(TaskItem task) => new(
        task.Title,
        $"{task.Category} ・ 優先度 {task.Priority} ・ {TaskFormat.DueLabel(task.DueDate)}",
        TaskFormat.PriorityBrush(task.Priority),
        task.Favorite ? Visibility.Visible : Visibility.Collapsed);
}
