using System.Windows;
using System.Windows.Controls;
using Seigetsu.Services;

namespace Seigetsu.Pages;

/// <summary>タイマーの表示を 0.5 秒ごとに更新したい画面。</summary>
public interface ITimerAware
{
    void OnTimerTick();
}

public partial class TimerPage : UserControl, IRefreshable, ITimerAware
{
    public TimerPage()
    {
        InitializeComponent();
    }

    private static FocusTimer Timer => App.Timer;

    private MainWindow Main => (MainWindow)Window.GetWindow(this);

    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();

    public void Refresh()
    {
        // 画面を作り直すときに OnModeChanged が動いてタイマーが止まらないよう、表示だけ合わせる
        ModeList.SelectionChanged -= OnModeChanged;
        ModeList.SelectedIndex = Timer.IsPomodoro ? 1 : 0;
        ModeList.SelectionChanged += OnModeChanged;

        var db = App.Database;
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
        else
        {
            PhaseText.Text = "ストップウォッチ";
            PhaseText.Foreground = ThemeService.Brush("AccentBrush");
            HintText.Text = "リセットすると、それまでの時間が作業記録として保存されます。";
            SkipButton.IsEnabled = false;
        }

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
        Timer.SetMode(ModeList.SelectedIndex == 1 ? TimerMode.Pomodoro : TimerMode.Stopwatch);
        Main.TimerChanged(recorded: true);
    }
}
