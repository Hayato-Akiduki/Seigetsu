using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Toolkit.Uwp.Notifications;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>通知の種類。種類ごとに見出し・アイコンの色・音を変える。</summary>
public enum ToastKind { Timer, Due, Classes, Study, Update, App, News }

/// <summary>通知に並べるボタン。押すと Action の名前が霽月に届く。</summary>
public sealed record ToastButtonSpec(string Label, string Action);

/// <summary>
/// Windows のトースト通知（見出し・アイコン・ボタン・音つき）。
/// 出せないとき（古い Windows や、通知の部品が動かないとき）は Show が false を返すので、
/// 呼ぶ側でタスクトレイのバルーンに切り替える。
/// </summary>
public static class Toasts
{
    /// <summary>通知の音の選択肢（設定の値と、画面に出す名前）。</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Sounds =
    [
        ("default", "標準"),
        ("reminder", "チャイム"),
        ("mail", "メール"),
        ("im", "メッセージ"),
        ("sms", "短い音"),
        ("silent", "鳴らさない"),
    ];

    private static Action<string>? _handler;
    private static string? _pending; // 霽月の画面ができる前に押された通知（通知から起動したとき）

    /// <summary>通知が押されたのを受け取れるようにする。アプリの起動の最初に一度だけ呼ぶ。</summary>
    public static void Initialize(Dispatcher dispatcher)
    {
        try
        {
            // 通知が押されると、ここが（画面とは別のスレッドで）呼ばれる
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                var action = args.TryGetValue("action", out var value) ? value : "open";
                dispatcher.BeginInvoke(() =>
                {
                    if (_handler is null)
                        _pending = action;
                    else
                        _handler(action);
                });
            };
        }
        catch (Exception)
        {
            // 通知の部品が使えない Windows では、押されたことを受け取れないだけにする
        }
    }

    /// <summary>押された通知の処理を決める。画面ができる前に押されていたら、すぐに処理する。</summary>
    public static void SetHandler(Action<string> handler)
    {
        _handler = handler;
        if (_pending is { } action)
        {
            _pending = null;
            handler(action);
        }
    }

    /// <summary>アンインストールのとき、Windows に登録した通知の情報を消す。</summary>
    public static void Uninstall()
    {
        try
        {
            ToastNotificationManagerCompat.Uninstall();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// 通知を出す。lines は本文の下に 1 行ずつ並べる一覧、note はいちばん下の小さな補足。
    /// 同じ種類の古い通知は、新しいもので置き換える。
    /// </summary>
    public static bool Show(ToastKind kind, string title, string body,
        IEnumerable<string>? lines = null, string? note = null,
        IReadOnlyList<ToastButtonSpec>? buttons = null, string clickAction = "open", string? soundOverride = null)
    {
        try
        {
            var (header, _, _) = Style(kind);
            var builder = new ToastContentBuilder()
                .AddArgument("action", clickAction)
                .AddHeader(KindId(kind), header, new ToastArguments().Add("action", clickAction))
                .AddText(title, hintMaxLines: 1)
                .AddText(body);

            if (IconPath(kind) is { } icon)
                builder.AddAppLogoOverride(new Uri(icon), ToastGenericAppLogoCrop.Circle);

            foreach (var line in lines ?? [])
            {
                builder.AddVisualChild(new AdaptiveGroup
                {
                    Children =
                    {
                        new AdaptiveSubgroup
                        {
                            Children = { new AdaptiveText { Text = "・ " + line, HintStyle = AdaptiveTextStyle.CaptionSubtle, HintWrap = true } },
                        },
                    },
                });
            }

            if (!string.IsNullOrEmpty(note))
                builder.AddAttributionText(note);

            foreach (var button in buttons ?? [])
            {
                // "dismiss" は通知を閉じるだけのボタン（霽月には何も届かない）
                if (button.Action == "dismiss")
                    builder.AddButton(new ToastButtonDismiss(button.Label));
                else
                    builder.AddButton(new ToastButton().SetContent(button.Label).AddArgument("action", button.Action));
            }

            var sound = soundOverride ?? SoundFor(kind);
            if (sound == "silent")
                builder.AddAudio(new ToastAudio { Silent = true });
            else
                builder.AddAudio(new Uri(SoundUri(sound)));

            builder.Show(toast =>
            {
                toast.Tag = KindId(kind);
                toast.Group = "seigetsu";
            });
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>種類ごとの音の設定。タイマー・締切・授業の変更・勉強の通知だけ選べて、ほかは標準。</summary>
    public static string SoundFor(ToastKind kind)
    {
        var key = SoundKey(kind);
        return key is null ? "default" : App.Database.GetSetting(key, "default");
    }

    public static string? SoundKey(ToastKind kind) => kind switch
    {
        ToastKind.Timer => SettingKeys.SoundTimer,
        ToastKind.Due => SettingKeys.SoundDue,
        ToastKind.Classes or ToastKind.News => SettingKeys.SoundClasses, // 大学からの知らせは同じ音
        ToastKind.Study => SettingKeys.SoundStudy,
        _ => null,
    };

    private static string SoundUri(string sound) => sound switch
    {
        "reminder" => "ms-winsoundevent:Notification.Reminder",
        "mail" => "ms-winsoundevent:Notification.Mail",
        "im" => "ms-winsoundevent:Notification.IM",
        "sms" => "ms-winsoundevent:Notification.SMS",
        _ => "ms-winsoundevent:Notification.Default",
    };

    private static string KindId(ToastKind kind) => kind.ToString().ToLowerInvariant();

    // 見出し・アイコンの字（Segoe Fluent Icons）・アイコンの色（落ち着いた色）
    private static (string Header, string Glyph, string Color) Style(ToastKind kind) => kind switch
    {
        ToastKind.Timer => ("集中タイマー", "\uE916", "#4A7C74"),
        ToastKind.Due => ("タスクの締切", "\uE787", "#B07D35"),
        ToastKind.Classes => ("授業の変更", "\uE7BE", "#A9544C"),
        ToastKind.Study => ("勉強のお知らせ", "\uE8F1", "#6B7F3E"),
        ToastKind.Update => ("アップデート", "\uE896", "#4A6FA5"),
        ToastKind.News => ("大学のお知らせ", "", "#7A5C99"),
        _ => ("霽月", "\uE946", "#5B5F97"),
    };

    /// <summary>種類ごとの丸いアイコン（色の円に白い記号）を PNG にして保存し、そのパスを返す。</summary>
    private static string? IconPath(ToastKind kind)
    {
        try
        {
            var dir = Path.Combine(AppPaths.DataDir, "toast-icons");
            var path = Path.Combine(dir, $"{KindId(kind)}-v1.png");
            if (File.Exists(path))
                return path;
            Directory.CreateDirectory(dir);

            const int size = 96;
            var (_, glyph, color) = Style(kind);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                dc.DrawEllipse(brush, null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
                var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    44, Brushes.White, 1.0);
                dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path))
                encoder.Save(stream);
            return path;
        }
        catch (Exception)
        {
            return null; // アイコンが作れなくても、通知は出す
        }
    }
}
