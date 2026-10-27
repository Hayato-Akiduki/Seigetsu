using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Seigetsu;

/// <summary>時計を画面いっぱいに出す。Esc キーかダブルクリックで閉じる。</summary>
public partial class ClockWindow : Window
{
    private static readonly CultureInfo Japanese = new("ja-JP");

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };

    // マウスを動かさずにいると、案内とマウスの矢印を隠す
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(3) };

    public ClockWindow(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        // 霽月の窓と同じ画面に出す（この位置の画面いっぱいに広がる）
        Left = owner.Left + 40;
        Top = owner.Top + 40;
        Width = 800;
        Height = 600;
        _tick.Tick += (_, _) => UpdateClock();
        _idle.Tick += (_, _) => HideHint();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateClock();
        _tick.Start();
        _idle.Start();
        // 時計を出しているあいだは、画面が暗くなったりスリープしたりしないようにする
        SetThreadExecutionState(EsContinuous | EsDisplayRequired);
        Activate();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _tick.Stop();
        _idle.Stop();
        SetThreadExecutionState(EsContinuous);
        Owner?.Activate();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        DateText.Text = now.ToString("yyyy年M月d日（ddd）", Japanese);
        TimeText.Text = now.ToString("HH:mm");
        SecondsText.Text = now.ToString("ss");

        var timer = App.Timer;
        if (timer.Running)
        {
            TimerText.Text = $"{(timer.IsPomodoro ? "ポモドーロ" : "ストップウォッチ")} ・ {timer.Status()}  {timer.Display()}";
            TimerText.Visibility = Visibility.Visible;
        }
        else
        {
            TimerText.Visibility = Visibility.Hidden;
        }
    }

    private void HideHint()
    {
        _idle.Stop();
        HintText.Visibility = Visibility.Hidden;
        Cursor = Cursors.None;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        HintText.Visibility = Visibility.Visible;
        Cursor = null;
        _idle.Stop();
        _idle.Start();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e) => Close();

    private const uint EsContinuous = 0x80000000;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
