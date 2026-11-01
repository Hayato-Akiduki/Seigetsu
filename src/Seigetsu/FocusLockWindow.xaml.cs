using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Seigetsu.Services;

namespace Seigetsu;

/// <summary>
/// 集中ロックの画面。モニター 1 台につき 1 枚出して、すべての画面を覆う。
/// 閉じられるのは FocusLock が閉じるときだけ（Alt+F4 などでは閉じない）。
/// </summary>
public partial class FocusLockWindow : Window
{
    private const double HoldSeconds = 3;

    private readonly System.Drawing.Rectangle _bounds; // 覆うモニターの範囲（物理ピクセル）
    private readonly bool _main;
    private readonly DispatcherTimer _hold = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private DateTime _holdStarted;
    private bool _allowClose;

    /// <summary>止めるボタンを長押しし終えたとき。</summary>
    public event Action? StopRequested;

    public FocusLockWindow(System.Drawing.Rectangle bounds, bool main)
    {
        InitializeComponent();
        _bounds = bounds;
        _main = main;
        // ほかの画面の窓は、出したときに手前を取り合わないようにする
        ShowActivated = main;
        if (!main)
        {
            ContentArea.Visibility = Visibility.Collapsed;
            SideText.Visibility = Visibility.Visible;
        }
        var image = main ? BackgroundImage.For(BackgroundPlace.Clock) : null;
        if (image is not null)
        {
            BackgroundLayer.Background = BackgroundImage.Fill(image);
            BackgroundLayer.Visibility = Visibility.Visible;
            BackgroundShade.Visibility = Visibility.Visible;
        }
        _hold.Tick += (_, _) => OnHoldTick();
        SourceInitialized += (_, _) => PlaceOnScreen();
    }

    public bool IsMain => _main;

    // 物理ピクセルで受け取ったモニターの範囲に合わせる（画面ごとに拡大率が違っても、ずれない）
    private void PlaceOnScreen()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        SetWindowPos(hwnd, HwndTopmost, _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, SwpNoActivate);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceOnScreen();
        // その画面で最大化して、タスクバーの上まで覆う
        WindowState = WindowState.Maximized;
        Refresh();
        if (_main)
            Activate();
    }

    /// <summary>時間の表示を合わせる（0.5 秒ごとに呼ばれる）。</summary>
    public void Refresh()
    {
        if (!_main)
            return;
        var timer = App.Timer;
        StatusText.Text = $"{timer.ModeName} ・ {timer.Status()}";
        TimeText.Text = timer.Display();
        Progress.Value = timer.Progress;
    }

    /// <summary>FocusLock から閉じるときに使う。</summary>
    public void CloseForReal()
    {
        _allowClose = true;
        Close();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
            e.Cancel = true;
    }

    private void OnClosed(object? sender, EventArgs e) => _hold.Stop();

    // ほかの窓が手前に来たら（通知や、タッチパッドの操作など）、すぐに取り戻す
    private void OnDeactivated(object? sender, EventArgs e)
    {
        if (_allowClose || !_main)
            return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (!_allowClose)
                BringToFront();
        });
    }

    public void BringToFront()
    {
        Topmost = false; // いったん外してから付け直すと、ほかの「常に手前」の窓より前に出る
        Topmost = true;
        Activate();
    }

    // ---- 長押しで止める -------------------------------------------------
    private void OnStopDown(object sender, MouseButtonEventArgs e)
    {
        _holdStarted = DateTime.UtcNow;
        StopButton.CaptureMouse();
        _hold.Start();
        e.Handled = true;
    }

    private void OnStopUp(object sender, MouseButtonEventArgs e) => CancelHold();

    private void OnStopLeave(object sender, MouseEventArgs e)
    {
        if (!StopButton.IsMouseCaptured)
            CancelHold();
    }

    private void CancelHold()
    {
        _hold.Stop();
        StopButton.ReleaseMouseCapture();
        StopFill.Width = 0;
        StopLabel.Text = "長押しで一時停止";
    }

    private void OnHoldTick()
    {
        // ボタンの外に指やマウスがずれたら、やり直し
        if (!StopButton.IsMouseOver)
        {
            CancelHold();
            return;
        }
        var progress = Math.Min(1, (DateTime.UtcNow - _holdStarted).TotalSeconds / HoldSeconds);
        StopFill.Width = StopButton.ActualWidth * progress;
        StopLabel.Text = $"そのまま押し続けて… {Math.Ceiling(HoldSeconds * (1 - progress)):0}";
        if (progress < 1)
            return;
        CancelHold();
        StopRequested?.Invoke();
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
