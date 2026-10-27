using System.Drawing;
using System.Windows.Forms;

namespace Seigetsu.Services;

/// <summary>
/// タスクトレイのアイコン（v6 と同じく、アプリが動いている間ずっと出しておく）。
/// アイコンをクリックするか「開く」で窓を出し、「終了」でアプリを終わる。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action onOpen, Action onQuit)
    {
        var menu = new ContextMenuStrip();
        var open = new ToolStripMenuItem("開く", null, (_, _) => onOpen()) { Font = new Font(SystemFonts.MenuFont!, FontStyle.Bold) };
        menu.Items.Add(open);
        menu.Items.Add(new ToolStripMenuItem("終了", null, (_, _) => onQuit()));

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "霽月 - Seigetsu",
            ContextMenuStrip = menu,
            Visible = true,
        };
        // 左クリックで開く（右クリックはメニュー）
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                onOpen();
        };
        _icon.BalloonTipClicked += (_, _) => onOpen();
    }

    /// <summary>タスクトレイからのお知らせ（Windows の通知として出る）。</summary>
    public void Notify(string title, string message) =>
        _icon.ShowBalloonTip(5000, title, message, ToolTipIcon.Info);

    private static Icon LoadIcon()
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/seigetsu.ico"));
        using var stream = resource!.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        // 消しておかないと、終わったあともタスクトレイにアイコンの跡が残ることがある
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}
