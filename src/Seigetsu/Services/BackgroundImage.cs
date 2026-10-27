using System.Windows.Media.Imaging;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>ホームの背景画像（v6 と同じ background_image の設定。画像のファイルの場所を入れる）。</summary>
public static class BackgroundImage
{
    public const string FileFilter = "画像 (*.png;*.jpg;*.jpeg;*.bmp;*.gif)|*.png;*.jpg;*.jpeg;*.bmp;*.gif";

    /// <summary>選んでいる画像の場所。選んでいない・ファイルが無くなったときは null。</summary>
    public static string? CurrentPath
    {
        get
        {
            var path = App.Database.GetSetting(SettingKeys.Background, "");
            return path != "" && File.Exists(path) ? path : null;
        }
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
