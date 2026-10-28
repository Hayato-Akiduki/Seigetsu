using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

    // 上帯のことばを切り替えるタイマーと、いま出していることば
    private readonly DispatcherTimer _messageTimer = new();
    private BannerMessage? _message;

    public HomePage()
    {
        InitializeComponent();
        _clock.Tick += (_, _) => UpdateClock();
        _messageTimer.Tick += (_, _) => ShowNextMessage();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // ホームを開いたときは、まずあいさつから
        ShowMessage(BannerMessages.Greeting(DateTime.Now), animate: false);
        UpdateClock();
        _clock.Start();
        App.Weather.Changed += UpdateWeather;
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _clock.Stop();
        _messageTimer.Stop();
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
        // あいさつを出しているあいだに時間帯が変わったら、あいさつも変える
        if (_message?.Kind == "greeting")
            GreetingText.Text = BannerMessages.GreetingText(now);
        DateText.Text = now.ToString("yyyy年M月d日（ddd）", Japanese);
        ClockText.Text = now.ToString("HH:mm");
    }

    private void OnMessageClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => ShowNextMessage();

    private void ShowNextMessage() => ShowMessage(BannerMessages.Next(_message?.Kind, DateTime.Now), animate: true);

    /// <summary>上帯のことばを出す。切り替えるときは、ふわっと出す。</summary>
    private void ShowMessage(BannerMessage message, bool animate)
    {
        _message = message;
        GreetingText.Text = message.Text;
        // 長いことばは少し小さくして、上帯が大きくなりすぎないようにする
        GreetingText.FontSize = message.Text.Length > 20 ? 21 : 26;
        SourceText.Text = message.Source is null ? "" : $"― {message.Source}";
        SourceText.Visibility = message.Source is null ? Visibility.Collapsed : Visibility.Visible;
        if (animate)
            MessagePanel.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)) { EasingFunction = new QuadraticEase() });
        RestartMessageTimer();
    }

    // 設定で間隔を変えたときにも合わせられるよう、毎回はかりなおす
    private void RestartMessageTimer()
    {
        _messageTimer.Stop();
        if (!IsLoaded)
            return;
        _messageTimer.Interval = TimeSpan.FromSeconds(Math.Max(10, BannerMessages.IntervalSeconds));
        _messageTimer.Start();
    }

    public void Refresh()
    {
        var db = App.Database;

        OpenTasksText.Text = $"{db.TaskCounts().Open} 件";
        TodayWorkText.Text = Format.Duration(db.WorkSecondsToday());
        TotalWorkText.Text = Format.Duration(db.WorkSecondsSince());

        UpdateWeather();
        UpdateBanner();

        var rows = db.TodayTasks().Select(ToRow).ToList();
        TaskList.ItemsSource = rows;
        NoTasksText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private object? _bannerImage;

    /// <summary>背景画像を選んでいれば見出しに敷く。選んでいなければ、ふつうの見出しに戻す。</summary>
    private void UpdateBanner()
    {
        var image = BackgroundImage.For(BackgroundPlace.Banner);
        // 同じ画像なら塗り直さない（データが変わるたびに呼ばれるので）
        if (image != _bannerImage)
        {
            _bannerImage = image;
            Banner.Background = image is null ? null : BackgroundImage.Fill(image);
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
            SourceText.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xE6, 0xF7));
            BigClockIcon.Foreground = new SolidColorBrush(Color.FromRgb(0xDC, 0xE6, 0xF7));
        }
        else
        {
            GreetingText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            ClockText.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            DateText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            SourceText.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            BigClockIcon.SetResourceReference(Wpf.Ui.Controls.SymbolIcon.ForegroundProperty, "MutedBrush");
        }
    }

    private static TaskRow ToRow(TaskItem task) => new(
        task.Title,
        $"{task.Category} ・ 優先度 {task.Priority} ・ {TaskFormat.DueLabel(task.DueDate)}",
        TaskFormat.PriorityBrush(task.Priority),
        task.Favorite ? Visibility.Visible : Visibility.Collapsed);
}
