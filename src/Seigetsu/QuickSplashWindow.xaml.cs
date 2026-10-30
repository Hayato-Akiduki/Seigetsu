using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Seigetsu;

/// <summary>
/// 2 回目からの起動アニメーション（約 1.5 秒）。画面の真ん中に小さく出して、
/// 満月が右上から欠けていき、アイコンと同じ三日月になったら Finished を出す。クリックかキーで飛ばせる。
/// </summary>
public partial class QuickSplashWindow : Window, ISplash
{
    public event EventHandler? Finished;

    private const double Total = 1.5;

    private DispatcherTimer? _endTimer;
    private bool _finished;

    public QuickSplashWindow()
    {
        InitializeComponent();
        // 月のまわりの光は、外観に合わせた月の色にする
        if (TryFindResource("MoonBrush") is SolidColorBrush moon)
        {
            var c = moon.Color;
            GlowInner.Color = Color.FromArgb(0x55, c.R, c.G, c.B);
            GlowOuter.Color = Color.FromArgb(0x00, c.R, c.G, c.B);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var rootScale = (ScaleTransform)Root.RenderTransform;
        var moonScale = (ScaleTransform)Moon.RenderTransform;
        var glowScale = (ScaleTransform)Glow.RenderTransform;

        // カードが浮かぶ
        Root.BeginAnimation(OpacityProperty, Anim(0, 1, 0, 0.25, ease));
        Scale(rootScale, 0.94, 1, 0, 0.35, ease);

        // 満月が出る
        Moon.BeginAnimation(OpacityProperty, Anim(0, 1, 0.1, 0.35, ease));
        Scale(moonScale, 0.9, 1, 0.1, 0.5, ease);
        Glow.BeginAnimation(OpacityProperty, Anim(0, 1, 0.15, 0.4, ease));
        Scale(glowScale, 0.7, 1, 0.15, 0.6, ease);

        // 右上から欠けて、アイコンと同じ三日月に
        Cut.BeginAnimation(EllipseGeometry.CenterProperty,
            new PointAnimation(new Point(157.5, -22.5), new Point(85, 39), TimeSpan.FromSeconds(0.75))
            {
                BeginTime = TimeSpan.FromSeconds(0.45),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            });
        Shade.BeginAnimation(OpacityProperty, Anim(0, 0.1, 0.6, 0.6, ease));

        _endTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Total) };
        _endTimer.Tick += (_, _) => Finish();
        _endTimer.Start();
    }

    private void OnSkip(object sender, InputEventArgs e) => Finish();

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _endTimer?.Stop();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    public void FadeOutAndClose()
    {
        Topmost = false;
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.3)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private static DoubleAnimation Anim(double from, double to, double begin, double seconds, IEasingFunction ease) =>
        new(from, to, TimeSpan.FromSeconds(seconds)) { BeginTime = TimeSpan.FromSeconds(begin), EasingFunction = ease };

    private static void Scale(ScaleTransform scale, double from, double to, double begin, double seconds, IEasingFunction ease)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(from, to, begin, seconds, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(from, to, begin, seconds, ease));
    }
}
