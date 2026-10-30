using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Path = System.Windows.Shapes.Path;

namespace Seigetsu;

/// <summary>起動アニメーションの窓。初回の大きな演出と、2 回目からの小さな演出で共通。</summary>
public interface ISplash
{
    /// <summary>アニメーションが終わった（または飛ばされた）。一度だけ出す。</summary>
    event EventHandler? Finished;

    void Show();

    /// <summary>ふわっと消してから閉じる。</summary>
    void FadeOutAndClose();
}

/// <summary>
/// 初回の起動アニメーション（画面全体で約 4.6 秒）。
/// 夜空 → 図形が月のまわりから集まって満月になる → 右上から影がかかって三日月になる → 名前が出る、の順に進む。
/// 終わったら Finished を出すので、そこでメインの窓を出し、<see cref="FadeOutAndClose"/> で消す。
/// クリックかキーで飛ばせる。
/// </summary>
public partial class SplashWindow : Window, ISplash
{
    /// <summary>アニメーションが終わった（または飛ばされた）。一度だけ出す。</summary>
    public event EventHandler? Finished;

    // 時間の区切り（秒）
    private const double FullMoon = 1.9;   // 図形が集まりきって、満月になるとき
    private const double WaneStart = 2.3;  // 右上から影がかかりはじめるとき
    private const double Crescent = 3.1;   // 三日月ができあがるとき
    private const double Total = 4.6;      // 全体の長さ

    private const int ShapeCount = 170;
    private const int StarCount = 110;

    // 色はアプリの月と同じ藍色でそろえる（濃さだけ少し変える）
    private static readonly Color MoonBlue = Color.FromRgb(0x8E, 0xA2, 0xFF);
    private static readonly Color[] ShapeColors =
    [
        Color.FromRgb(0x54, 0x68, 0xE0),
        Color.FromRgb(0x6F, 0x86, 0xF5),
        Color.FromRgb(0x7F, 0x95, 0xFA),
        Color.FromRgb(0x8E, 0xA2, 0xFF),
        Color.FromRgb(0xA5, 0xB5, 0xFF),
    ];

    private readonly Random _rand = new();
    private readonly List<Action> _starters = [];
    private readonly double _w;
    private readonly double _h;
    private readonly Point _center;
    private readonly double _moonR;
    private DispatcherTimer? _endTimer;
    private bool _finished;

    public SplashWindow()
    {
        InitializeComponent();

        // いちばん前の画面全体をおおう
        _w = SystemParameters.PrimaryScreenWidth;
        _h = SystemParameters.PrimaryScreenHeight;
        Left = 0;
        Top = 0;
        Width = _w;
        Height = _h;
        _center = new Point(_w / 2, _h * 0.42);
        _moonR = Math.Min(_w, _h) * 0.14;

        BuildStars();
        BuildGlow();
        BuildShapes();
        BuildMoon();
        BuildTitle();

        _starters.Add(() =>
        {
            Fade(Sky, 0, 1, 0, 0.6);
            Fade(SkipHint, 0, 0.8, 0.8, 0.6);
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // すべての動きを、同じ瞬間から始める
        foreach (var start in _starters)
            start();

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

    /// <summary>ふわっと消してから閉じる。</summary>
    public void FadeOutAndClose()
    {
        Topmost = false;
        IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.5)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    // ───── 夜空の星 ─────

    private void BuildStars()
    {
        var starBrush = Frozen(Color.FromRgb(0xE3, 0xE8, 0xFF));
        for (var i = 0; i < StarCount; i++)
        {
            var size = 1 + _rand.NextDouble() * 2.2;
            var star = new Ellipse { Width = size, Height = size, Fill = starBrush, Opacity = 0 };
            Canvas.SetLeft(star, _rand.NextDouble() * _w);
            Canvas.SetTop(star, _rand.NextDouble() * _h);
            Stage.Children.Add(star);

            // ぽつぽつと灯って、一度またたく
            var peak = 0.3 + _rand.NextDouble() * 0.55;
            var t0 = 0.1 + _rand.NextDouble() * 1.6;
            var blink = t0 + 0.6 + _rand.NextDouble() * 2.4;
            var anim = Keys(
                (0, 0), (t0, 0), (t0 + 0.5, peak),
                (blink, peak), (blink + 0.25, peak * 0.25), (blink + 0.55, peak));
            _starters.Add(() => star.BeginAnimation(OpacityProperty, anim));
        }
    }

    // ───── 月の光（にじむ光・満月のときの光・三日月ができたときの輪） ─────

    private void BuildGlow()
    {
        // 月のまわりの大きな光。満月で強く、三日月になると少し落ち着く
        var glow = Circle(_moonR * 3.2, Radial(Color.FromArgb(0x60, 0x8E, 0xA2, 0xFF), Color.FromArgb(0x00, 0x34, 0x51, 0xD1)));
        var glowScale = Place(glow, _center, 0.4);
        Stage.Children.Add(glow);

        // 満月になった瞬間の、やわらかい光
        var flash = Circle(_moonR * 1.9, Radial(Color.FromArgb(0x90, 0xDC, 0xE3, 0xFF), Color.FromArgb(0x00, 0x8E, 0xA2, 0xFF)));
        var flashScale = Place(flash, _center, 0.6);
        Stage.Children.Add(flash);

        // 三日月ができたときに広がる輪
        var ring = new Ellipse
        {
            Width = _moonR * 2,
            Height = _moonR * 2,
            Stroke = Frozen(Color.FromRgb(0xA5, 0xB5, 0xFF)),
            StrokeThickness = 1.5,
            Opacity = 0,
        };
        var ringScale = Place(ring, _center, 1);
        Stage.Children.Add(ring);

        _starters.Add(() =>
        {
            glow.BeginAnimation(OpacityProperty, Keys((0, 0), (0.6, 0), (FullMoon, 0.8), (FullMoon + 0.3, 1), (Crescent, 0.75)));
            Scale(glowScale, 0.4, 1, 0.6, FullMoon - 0.3, new CubicEase { EasingMode = EasingMode.EaseOut });

            flash.BeginAnimation(OpacityProperty, Keys((0, 0), (FullMoon - 0.1, 0), (FullMoon + 0.1, 0.9), (FullMoon + 0.8, 0)));
            Scale(flashScale, 0.6, 1.5, FullMoon - 0.1, 0.9, new CubicEase { EasingMode = EasingMode.EaseOut });

            ring.BeginAnimation(OpacityProperty, Keys((0, 0), (Crescent - 0.1, 0), (Crescent, 0.7), (Crescent + 1.2, 0)));
            Scale(ringScale, 1, 4.5, Crescent - 0.1, 1.3, new CubicEase { EasingMode = EasingMode.EaseOut });
        });
    }

    // ───── 月のまわりから集まって満月になる図形 ─────

    private void BuildShapes()
    {
        var geometries = new[] { Unit.Circle, Unit.Square, Unit.Triangle, Unit.Diamond, Unit.Star, Unit.Hexagon };
        var r = _moonR;

        foreach (var target in SampleDisk(ShapeCount))
        {
            var size = r * (0.16 + _rand.NextDouble() * 0.12);
            var shape = new Path
            {
                Data = geometries[_rand.Next(geometries.Length)],
                Fill = Frozen(ShapeColors[_rand.Next(ShapeColors.Length)]),
                Opacity = 0,
            };
            var scale = new ScaleTransform(size * 0.3, size * 0.3);
            var rotate = new RotateTransform(_rand.NextDouble() * 360);
            var move = new TranslateTransform();
            shape.RenderTransform = new TransformGroup { Children = { scale, rotate, move } };
            Stage.Children.Add(shape);

            // 目的の場所と同じ向きの、月から少し離れたところから、ゆるく弧を描いて入ってくる
            var rt = Math.Sqrt(target.X * target.X + target.Y * target.Y);
            var at = Math.Atan2(target.Y, target.X);
            var turn = Math.PI * (0.25 + _rand.NextDouble() * 0.15);
            var a0 = at - turn;
            var r0 = r * (2.4 + _rand.NextDouble() * 1.4);

            // 月のまん中に近いものから先に集まり、月が内から外へ育っていくように
            var delay = 0.35 + rt / r * 0.55 + _rand.NextDouble() * 0.15;
            var duration = Math.Min(0.9 + _rand.NextDouble() * 0.2, FullMoon - 0.05 - delay);
            const int steps = 20;
            var xs = new (double, double)[steps + 2];
            var ys = new (double, double)[steps + 2];
            xs[0] = (0, _center.X + r0 * Math.Cos(a0));
            ys[0] = (0, _center.Y + r0 * Math.Sin(a0));
            for (var k = 0; k <= steps; k++)
            {
                var u = (double)k / steps;
                var radius = r0 + (rt - r0) * EaseOut(u, 3);
                var angle = a0 + turn * EaseOut(u, 2.5);
                var px = _center.X + radius * Math.Cos(angle);
                var py = _center.Y + radius * Math.Sin(angle);
                xs[k + 1] = (delay + u * duration, px);
                ys[k + 1] = (delay + u * duration, py);
            }
            var arrive = delay + duration;
            var finalAngle = rotate.Angle + (_rand.NextDouble() < 0.5 ? 90 : -90);

            var xAnim = Keys(xs);
            var yAnim = Keys(ys);
            var opacity = Keys((0, 0), (delay, 0), (delay + 0.35, 0.95), (FullMoon, 0.95), (FullMoon + 0.35, 0));
            var sizeAnim = Keys((0, size * 0.3), (delay, size * 0.3), (arrive, size), (FullMoon, size), (FullMoon + 0.35, size * 0.6));
            var angleAnim = new DoubleAnimation(rotate.Angle, finalAngle, TimeSpan.FromSeconds(duration))
            {
                BeginTime = TimeSpan.FromSeconds(delay),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };

            _starters.Add(() =>
            {
                move.BeginAnimation(TranslateTransform.XProperty, xAnim);
                move.BeginAnimation(TranslateTransform.YProperty, yAnim);
                shape.BeginAnimation(OpacityProperty, opacity);
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, sizeAnim);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, sizeAnim);
                rotate.BeginAnimation(RotateTransform.AngleProperty, angleAnim);
            });
        }
    }

    /// <summary>満月の円の中に、なるべく重なりすぎないように点を散らす（月の中心からの位置）。</summary>
    private List<Point> SampleDisk(int count)
    {
        var r = _moonR * 0.95;
        var points = new List<Point>();
        var minDistance = _moonR * 0.135;
        for (var attempt = 0; attempt < 20000 && points.Count < count; attempt++)
        {
            var x = (_rand.NextDouble() * 2 - 1) * r;
            var y = (_rand.NextDouble() * 2 - 1) * r;
            if (x * x + y * y > r * r)
                continue;
            // 何度やっても入らないときは、間を少しつめる
            var limit = attempt < 8000 ? minDistance : minDistance * 0.6;
            if (points.Any(p => (p.X - x) * (p.X - x) + (p.Y - y) * (p.Y - y) < limit * limit))
                continue;
            points.Add(new Point(x, y));
        }
        return points;
    }

    // ───── 月（満月から、右上が欠けて三日月に） ─────

    private void BuildMoon()
    {
        var r = _moonR;
        var fill = new LinearGradientBrush
        {
            StartPoint = new Point(1, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0xDC, 0xE3, 0xFF), 0),
                new GradientStop(MoonBlue, 0.5),
                new GradientStop(Color.FromRgb(0x54, 0x68, 0xE0), 1),
            },
        };
        fill.Freeze();

        // 欠けたところに、うっすら残る月の影
        var shade = new Ellipse { Width = r * 2, Height = r * 2, Fill = Frozen(MoonBlue), Opacity = 0 };
        Place(shade, _center, 1);
        Stage.Children.Add(shade);

        // くり抜く円を、右上の外から三日月の位置まで動かして欠けさせる
        var cutFrom = new Point(r * 1.95, -r * 1.65);
        var cutTo = new Point(r * 0.5, -r * 0.42);
        var cut = new EllipseGeometry(cutFrom, r * 0.83, r * 0.83);
        var moon = new Path
        {
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(0, 0), r, r), cut),
            Fill = fill,
            Opacity = 0,
        };
        var scale = new ScaleTransform(0.96, 0.96);
        moon.RenderTransform = new TransformGroup { Children = { scale, new TranslateTransform(_center.X, _center.Y) } };
        Stage.Children.Add(moon);

        _starters.Add(() =>
        {
            Fade(moon, 0, 1, FullMoon - 0.15, 0.5);
            Scale(scale, 0.96, 1, FullMoon - 0.15, 0.8, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 });
            cut.BeginAnimation(EllipseGeometry.CenterProperty,
                new PointAnimation(cutFrom, cutTo, TimeSpan.FromSeconds(Crescent - WaneStart))
                {
                    BeginTime = TimeSpan.FromSeconds(WaneStart),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                });
            Fade(shade, 0, 0.07, WaneStart + 0.2, Crescent - WaneStart);
        });
    }

    // ───── 名前 ─────

    private void BuildTitle()
    {
        // 少し透ける、白から淡い青へのグラデーション。まわりにうっすら青い光をにじませる
        var titleFill = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xF0, 0xF5, 0xF6, 0xFF), 0),
                new GradientStop(Color.FromArgb(0xB8, 0xB4, 0xC1, 0xFF), 1),
            },
        };
        titleFill.Freeze();
        var title = new TextBlock
        {
            Text = "S E I G E T S U",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = Math.Max(32, _moonR * 0.32),
            FontWeight = FontWeights.SemiBold,
            Foreground = titleFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = MoonBlue,
                BlurRadius = 22,
                ShadowDepth = 0,
                Opacity = 0.75,
            },
        };
        var line = new Border
        {
            Width = 240,
            Height = 1.5,
            Margin = new Thickness(0, 18, 0, 0),
            Background = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x00, 0x8E, 0xA2, 0xFF), 0),
                    new GradientStop(MoonBlue, 0.5),
                    new GradientStop(Color.FromArgb(0x00, 0x8E, 0xA2, 0xFF), 1),
                },
            },
        };
        var titleMove = new TranslateTransform(0, 14);
        var lineScale = new ScaleTransform(0, 1);
        title.RenderTransform = titleMove;
        title.Opacity = 0;
        line.RenderTransform = lineScale;
        line.RenderTransformOrigin = new Point(0.5, 0.5);

        // 画面の幅いっぱいの入れ物に入れて、まん中にそろえる
        var panel = new StackPanel { Width = _w, Children = { title, line } };
        Canvas.SetTop(panel, _center.Y + _moonR * 1.5);
        Stage.Children.Add(panel);

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        _starters.Add(() =>
        {
            Fade(title, 0, 1, Crescent + 0.1, 0.7);
            titleMove.BeginAnimation(TranslateTransform.YProperty, Anim(14, 0, Crescent + 0.1, 0.8, ease));
            lineScale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(0, 1, Crescent + 0.35, 0.8, ease));
        });
    }

    // ───── 小さな道具 ─────

    private static double EaseOut(double u, double power) => 1 - Math.Pow(1 - u, power);

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush Radial(Color inner, Color outer)
    {
        var brush = new RadialGradientBrush(inner, outer);
        brush.Freeze();
        return brush;
    }

    private static Ellipse Circle(double radius, Brush fill) =>
        new() { Width = radius * 2, Height = radius * 2, Fill = fill, Opacity = 0 };

    /// <summary>図形のまん中を point に置き、拡大の変形を返す。</summary>
    private static ScaleTransform Place(FrameworkElement element, Point point, double initialScale)
    {
        var scale = new ScaleTransform(initialScale, initialScale);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = scale;
        Canvas.SetLeft(element, point.X - element.Width / 2);
        Canvas.SetTop(element, point.Y - element.Height / 2);
        return scale;
    }

    private static DoubleAnimation Anim(double from, double to, double begin, double seconds, IEasingFunction? ease = null) =>
        new(from, to, TimeSpan.FromSeconds(seconds)) { BeginTime = TimeSpan.FromSeconds(begin), EasingFunction = ease };

    private static void Fade(UIElement element, double from, double to, double begin, double seconds) =>
        element.BeginAnimation(OpacityProperty, Anim(from, to, begin, seconds));

    private static void Scale(ScaleTransform scale, double from, double to, double begin, double seconds, IEasingFunction ease)
    {
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim(from, to, begin, seconds, ease));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim(from, to, begin, seconds, ease));
    }

    /// <summary>（秒, 値）の組を順につなぐアニメーション。</summary>
    private static DoubleAnimationUsingKeyFrames Keys(params (double Time, double Value)[] keys)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        foreach (var (time, value) in keys)
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(time))));
        return anim;
    }

    /// <summary>大きさ 1 の図形（まん中が原点）。</summary>
    private static class Unit
    {
        public static readonly Geometry Circle = Freeze(new EllipseGeometry(new Point(0, 0), 0.5, 0.5));
        public static readonly Geometry Square = Freeze(new RectangleGeometry(new Rect(-0.42, -0.42, 0.84, 0.84), 0.08, 0.08));
        public static readonly Geometry Triangle = Polygon(3, 0.6, 0.6);
        public static readonly Geometry Diamond = Freeze(Geometry.Parse("M 0,-0.62 L 0.42,0 L 0,0.62 L -0.42,0 Z"));
        public static readonly Geometry Star = Polygon(5, 0.66, 0.28);
        public static readonly Geometry Hexagon = Polygon(6, 0.55, 0.55);

        /// <summary>正多角形（outer = inner）か星形（outer ≠ inner）。</summary>
        private static Geometry Polygon(int corners, double outer, double inner)
        {
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                var star = Math.Abs(outer - inner) > 1e-6;
                var count = star ? corners * 2 : corners;
                for (var i = 0; i < count; i++)
                {
                    var radius = star && i % 2 == 1 ? inner : outer;
                    var angle = -Math.PI / 2 + Math.PI * 2 * i / count;
                    var point = new Point(radius * Math.Cos(angle), radius * Math.Sin(angle));
                    if (i == 0)
                        g.BeginFigure(point, true, true);
                    else
                        g.LineTo(point, true, false);
                }
            }
            return Freeze(geometry);
        }

        private static Geometry Freeze(Geometry geometry)
        {
            geometry.Freeze();
            return geometry;
        }
    }
}
