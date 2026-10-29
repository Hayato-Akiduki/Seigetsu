using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Seigetsu.Services;

namespace Seigetsu.Pages;

/// <summary>タイマーの表示を 0.5 秒ごとに更新したい画面。</summary>
public interface ITimerAware
{
    void OnTimerTick();
}

public partial class TimerPage : UserControl, IRefreshable, ITimerAware
{
    // タイマーですぐ選べる長さ（秒）
    private static readonly int[] Presets = [60, 3 * 60, 5 * 60, 10 * 60, 15 * 60, 30 * 60, 60 * 60];

    private static readonly TimerMode[] Modes = [TimerMode.Stopwatch, TimerMode.Pomodoro, TimerMode.Countdown];

    public TimerPage()
    {
        InitializeComponent();
        foreach (var seconds in Presets)
        {
            var button = new Wpf.Ui.Controls.Button
            {
                Content = FocusTimer.LengthLabel(seconds),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(4),
                Tag = seconds,
            };
            button.Click += (_, _) => SetLength(seconds);
            PresetPanel.Children.Add(button);
        }
    }

    private static FocusTimer Timer => App.Timer;

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    public void Refresh()
    {
        // 画面を作り直すときに OnModeChanged が動いてタイマーが止まらないよう、表示だけ合わせる
        ModeList.SelectionChanged -= OnModeChanged;
        ModeList.SelectedIndex = Array.IndexOf(Modes, Timer.Mode);
        ModeList.SelectionChanged += OnModeChanged;

        var db = App.Database;
        ShowLength();
        TodayText.Text = Format.Duration(db.WorkSecondsToday());
        WeekText.Text = Format.Duration(db.WorkSecondsSince(DateTime.Today.AddDays(-6).ToString("yyyy-MM-dd")));
        OnTimerTick();
    }

    public void OnTimerTick()
    {
        TimeText.Text = Timer.Display();
        Progress.Value = Timer.Progress;

        if (Timer.IsPomodoro)
        {
            var work = Timer.Phase == TimerPhase.Work;
            PhaseText.Text = $"{FocusTimer.PhaseName(Timer.Phase)}の時間";
            PhaseText.Foreground = ThemeService.Brush(work ? "AccentBrush" : "SuccessBrush");
            HintText.Text = $"作業 {Timer.WorkSeconds / 60}分 → 休憩 {Timer.BreakSeconds / 60}分 をくり返します。時間は「設定」で変えられます。";
            SkipButton.IsEnabled = true;
        }
        else if (Timer.IsCountdown)
        {
            PhaseText.Text = $"タイマー（{FocusTimer.LengthLabel(Timer.CountdownSeconds)}）";
            PhaseText.Foreground = ThemeService.Brush("AccentBrush");
            HintText.Text = "時間が来たら、音と Windows の通知で知らせます。タイマーの時間は作業の記録には入りません。";
            SkipButton.IsEnabled = false;
        }
        else
        {
            PhaseText.Text = "ストップウォッチ";
            PhaseText.Foreground = ThemeService.Brush("AccentBrush");
            HintText.Text = "リセットすると、それまでの時間が作業記録として保存されます。";
            SkipButton.IsEnabled = false;
        }

        CountdownPanel.Visibility = Timer.IsCountdown ? Visibility.Visible : Visibility.Collapsed;
        // 動いている間に長さを変えると最初からになってしまうので、止めているときだけ変えられる
        CountdownPanel.IsEnabled = !Timer.Running;

        var label = Timer.Running ? "一時停止" : Timer.IsIdle ? "開始" : "再開";
        if (!Equals(StartButton.Content, label))
        {
            StartButton.Content = label;
            StartButton.Icon = new Wpf.Ui.Controls.SymbolIcon(Timer.Running
                ? Wpf.Ui.Controls.SymbolRegular.Pause24
                : Wpf.Ui.Controls.SymbolRegular.Play24);
        }
    }

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        Timer.Toggle();
        Main.TimerChanged();
    }

    private void OnResetClick(object sender, RoutedEventArgs e)
    {
        Timer.Reset();
        Main.TimerChanged(recorded: true);
    }

    private void OnSkipClick(object sender, RoutedEventArgs e)
    {
        Timer.SkipPhase();
        Main.TimerChanged(recorded: true);
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
            return;
        // モードを変えると、それまでの時間は記録されてリセットされる（v6 と同じ）
        Timer.SetMode(Modes[Math.Max(0, ModeList.SelectedIndex)]);
        Main.TimerChanged(recorded: true);
    }

    // ---- タイマーの長さ -------------------------------------------------
    private void SetLength(int seconds)
    {
        Timer.SetCountdown(seconds);
        ShowLength();
        Main.TimerChanged();
    }

    /// <summary>入力欄に今の長さを出す。</summary>
    private void ShowLength()
    {
        var seconds = Timer.CountdownSeconds;
        MinutesBox.Text = (seconds / 60).ToString();
        SecondsBox.Text = (seconds % 60).ToString();
    }

    private void OnApplyLengthClick(object sender, RoutedEventArgs e) => ApplyLength();

    private void OnLengthKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        ApplyLength();
        e.Handled = true;
    }

    private void ApplyLength()
    {
        // 空の欄は 0 とみなす
        var minutesOk = int.TryParse(MinutesBox.Text.Trim() is { Length: > 0 } m ? m : "0", out var minutes);
        var secondsOk = int.TryParse(SecondsBox.Text.Trim() is { Length: > 0 } s ? s : "0", out var seconds);
        var total = minutes * 60 + seconds;
        if (!minutesOk || !secondsOk || minutes < 0 || seconds is < 0 or > 59 || total < 1 || total > FocusTimer.MaxCountdownSeconds)
        {
            Main.Notify("長さは 1 秒から 1439 分 59 秒までの数で入れてください（秒は 0〜59）。", Wpf.Ui.Controls.ControlAppearance.Danger, 5);
            ShowLength();
            return;
        }
        SetLength(total);
    }
}
