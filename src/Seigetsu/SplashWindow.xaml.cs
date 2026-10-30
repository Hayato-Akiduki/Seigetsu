using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Seigetsu;

/// <summary>
/// 起動アニメーション（約 1.8 秒）。終わったら Finished を出すので、そこでメインの窓を出し、
/// そのあと <see cref="FadeOutAndClose"/> で消す。クリックかキーで飛ばせる。
/// </summary>
public partial class SplashWindow : Window
{
    /// <summary>アニメーションが終わった（または飛ばされた）。一度だけ出す。</summary>
    public event EventHandler? Finished;

    private Storyboard? _story;
    private bool _finished;

    public SplashWindow()
    {
        InitializeComponent();
        // 月のまわりの光は、外観に合わせた月の色にする
        if (TryFindResource("MoonBrush") is SolidColorBrush moon)
        {
            var c = moon.Color;
            GlowInner.Color = Color.FromArgb(0x66, c.R, c.G, c.B);
            GlowOuter.Color = Color.FromArgb(0x00, c.R, c.G, c.B);
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var s = new Storyboard();
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var soft = new SineEase { EasingMode = EasingMode.EaseInOut };

        // 窓が浮かび上がる
        Add(s, Root, "Opacity", 0, 1, 0.0, 0.35, ease);
        Add(s, Root, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", 0.96, 1, 0.0, 0.45, ease);
        Add(s, Root, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", 0.96, 1, 0.0, 0.45, ease);

        // 雨雲が左右に晴れていく
        Add(s, CloudLeft, "(UIElement.RenderTransform).(TranslateTransform.X)", 0, -80, 0.25, 0.9, soft);
        Add(s, CloudRight, "(UIElement.RenderTransform).(TranslateTransform.X)", 0, 80, 0.25, 0.9, soft);
        Add(s, CloudLeft, "Opacity", 1, 0, 0.35, 0.8, soft);
        Add(s, CloudRight, "Opacity", 1, 0, 0.35, 0.8, soft);

        // 月が現れて、光がにじむ
        Add(s, Moon, "Opacity", 0, 1, 0.3, 0.7, ease);
        Add(s, Moon, "(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleX)", 0.85, 1, 0.3, 0.9, ease);
        Add(s, Moon, "(UIElement.RenderTransform).(TransformGroup.Children)[0].(ScaleTransform.ScaleY)", 0.85, 1, 0.3, 0.9, ease);
        Add(s, Moon, "(UIElement.RenderTransform).(TransformGroup.Children)[1].(RotateTransform.Angle)", -18, 0, 0.3, 0.9, ease);
        Add(s, Glow, "Opacity", 0, 1, 0.45, 0.8, ease);
        Add(s, Glow, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", 0.6, 1.25, 0.45, 1.0, ease);
        Add(s, Glow, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", 0.6, 1.25, 0.45, 1.0, ease);

        // 名前が下から浮かぶ
        Add(s, TitleText, "Opacity", 0, 1, 0.8, 0.5, ease);
        Add(s, TitleText, "(UIElement.RenderTransform).(TranslateTransform.Y)", 10, 0, 0.8, 0.5, ease);
        Add(s, SubText, "Opacity", 0, 1, 1.0, 0.5, ease);
        Add(s, SubText, "(UIElement.RenderTransform).(TranslateTransform.Y)", 8, 0, 1.0, 0.5, ease);
        Add(s, Line, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", 0, 1, 1.1, 0.6, ease);

        // 最後に少しだけ止めてから終わる
        s.Duration = TimeSpan.FromSeconds(1.85);
        s.Completed += (_, _) => Finish();
        _story = s;
        s.Begin(this, true);
    }

    private static void Add(Storyboard s, DependencyObject target, string property,
        double from, double to, double begin, double seconds, IEasingFunction ease)
    {
        var a = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            BeginTime = TimeSpan.FromSeconds(begin),
            EasingFunction = ease,
        };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, new PropertyPath(property));
        s.Children.Add(a);
    }

    private void OnSkip(object sender, InputEventArgs e)
    {
        // 飛ばしたときは、最後の形にしてから終わる
        _story?.SkipToFill(this);
        Finish();
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>ふわっと消してから閉じる。</summary>
    public void FadeOutAndClose()
    {
        Topmost = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.3)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }
}
