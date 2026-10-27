using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>スクリーンショット（v6 と同じく、すべての画面をまとめて 1 枚の PNG にする）。</summary>
public static class Screenshot
{
    /// <summary>保存先。v6 と同じく「ピクチャ\Seigetsu」。ピクチャが無ければデータのフォルダの中。</summary>
    public static string Folder
    {
        get
        {
            var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            return Directory.Exists(pictures)
                ? Path.Combine(pictures, "Seigetsu")
                : Path.Combine(AppPaths.DataDir, "screenshots");
        }
    }

    /// <summary>いまの画面を撮って保存し、保存した場所を返す。</summary>
    public static string Capture()
    {
        // すべてのモニターを合わせた範囲
        var bounds = SystemInformation.VirtualScreen;
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);

        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"seigetsu_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        bitmap.Save(path, ImageFormat.Png);
        return path;
    }
}
