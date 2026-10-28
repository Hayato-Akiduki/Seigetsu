using System.Windows.Media;
using System.Windows.Media.Imaging;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>背景画像を出す場所。</summary>
public enum BackgroundPlace
{
    Banner, // ホームの上帯
    Window, // 窓全体（右側の中身の所）
    Clock,  // 時計の大画面
}

/// <summary>
/// 背景画像（v6 と同じ background_image の設定。画像のファイルの場所を入れる）。
/// v7 では、ホームの上帯・窓全体・時計の大画面のうち、選んだ所に出す。
/// </summary>
public static class BackgroundImage
{
    public const string FileFilter = "画像 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif";

    // 同じ画像を何か所にも出すので、読んだものを 1 つだけ取っておく
    private static string? _cachedPath;
    private static BitmapSource? _cachedImage;

    /// <summary>選んでいる画像の場所。選んでいない・ファイルが無くなったときは null。</summary>
    public static string? CurrentPath
    {
        get
        {
            var path = App.Database.GetSetting(SettingKeys.Background, "");
            return path != "" && File.Exists(path) ? path : null;
        }
    }

    /// <summary>選んでいる画像。選んでいない・読めないときは null。</summary>
    public static BitmapSource? Current
    {
        get
        {
            var path = CurrentPath;
            if (path != _cachedPath)
            {
                _cachedPath = path;
                _cachedImage = path is null ? null : Load(path);
            }
            return _cachedImage;
        }
    }

    /// <summary>その場所に画像を出すことになっていれば、その画像。出さないときは null。</summary>
    public static BitmapSource? For(BackgroundPlace place) => IsShownIn(place) ? Current : null;

    /// <summary>その場所に出すかどうか（はじめは、ホームの上帯には出さず、窓全体と時計の大画面に出す）。</summary>
    public static bool IsShownIn(BackgroundPlace place) =>
        App.Database.GetSetting(KeyFor(place), place == BackgroundPlace.Banner ? "0" : "1") == "1";

    public static void SetShownIn(BackgroundPlace place, bool shown) =>
        App.Database.SetSetting(KeyFor(place), shown ? "1" : "0");

    private static string KeyFor(BackgroundPlace place) => place switch
    {
        BackgroundPlace.Banner => SettingKeys.BackgroundBanner,
        BackgroundPlace.Window => SettingKeys.BackgroundWindow,
        _ => SettingKeys.BackgroundClock,
    };

    /// <summary>
    /// 窓全体に敷いた画像の上に重ねる、背景色の膜の濃さ（0〜1）。大きいほど画像はうすく見え、文字が読みやすい。
    /// </summary>
    public static double WindowVeil => App.Database.GetSetting(SettingKeys.BackgroundStrength, "NORMAL") switch
    {
        "STRONG" => 0.5,
        "WEAK" => 0.85,
        _ => 0.7,
    };

    /// <summary>画像を、はみ出す所を切って枠いっぱいに敷くための塗り。</summary>
    public static ImageBrush Fill(BitmapSource image)
    {
        var brush = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
        brush.Freeze();
        return brush;
    }

    /// <summary>画像を読む。読めなければ null。読んだあとはファイルを開いたままにしない。</summary>
    public static BitmapSource? Load(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.DecodePixelWidth = 1920; // 大きな写真でもメモリを使いすぎないように
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or UriFormatException
                                          or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
