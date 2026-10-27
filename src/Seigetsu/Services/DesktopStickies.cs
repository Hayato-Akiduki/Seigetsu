using System.Windows;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>
/// デスクトップに出している付箋の窓をまとめて受け持つ。どれを・どこに出すかは desktop_stickies の表に保存し、
/// 次に起動したときも同じ場所に出す。
/// </summary>
public sealed class DesktopStickies
{
    private const double DefaultWidth = 260;
    private const double DefaultHeight = 200;

    private readonly Dictionary<long, DesktopStickyWindow> _windows = [];
    private bool _closingAll;

    public bool IsShown(long stickyId) => _windows.ContainsKey(stickyId);

    /// <summary>表の内容に窓を合わせる（新しく出す・閉じる・中身と色を直す）。データが変わるたびに呼ばれる。</summary>
    public void Refresh()
    {
        if (_closingAll)
            return;
        var db = App.Database;
        var placements = db.GetDesktopStickies();
        var notes = db.GetStickies().ToDictionary(n => n.Id);
        var wanted = placements.Select(p => p.StickyId).ToHashSet();

        foreach (var id in _windows.Keys.Where(id => !wanted.Contains(id)).ToList())
            CloseWindow(id);
        foreach (var placement in placements)
        {
            if (!notes.TryGetValue(placement.StickyId, out var note))
                continue;
            if (_windows.TryGetValue(note.Id, out var window))
                window.Update(note);
            else
                Open(note, placement);
        }
    }

    /// <summary>デスクトップに出す。画面の右上から、少しずつずらして置く。</summary>
    public void Show(long stickyId)
    {
        if (!IsShown(stickyId))
        {
            var area = SystemParameters.WorkArea;
            var step = _windows.Count % 8 * 28;
            App.Database.ShowStickyOnDesktop(stickyId,
                area.Right - DefaultWidth - 24 - step, area.Top + 24 + step, DefaultWidth, DefaultHeight);
            Refresh();
        }
        if (_windows.TryGetValue(stickyId, out var window))
            window.Activate();
        NotifyMain();
    }

    /// <summary>デスクトップから外す（付箋そのものは残る）。</summary>
    public void Hide(long stickyId)
    {
        App.Database.RemoveStickyFromDesktop(stickyId);
        CloseWindow(stickyId);
        NotifyMain();
    }

    public void Toggle(long stickyId)
    {
        if (IsShown(stickyId))
            Hide(stickyId);
        else
            Show(stickyId);
    }

    /// <summary>アプリを終えるとき。表はそのまま残すので、次に起動したときにまた出る。</summary>
    public void CloseAll()
    {
        _closingAll = true;
        foreach (var window in _windows.Values.ToList())
            window.Close();
        _windows.Clear();
    }

    private void Open(StickyNote note, DesktopSticky placement)
    {
        var window = new DesktopStickyWindow(note, KeepOnScreen(placement));
        // Alt+F4 などで閉じられたときも、デスクトップから外したことにする
        window.Closed += (_, _) =>
        {
            if (_windows.Remove(note.Id) && !_closingAll)
            {
                App.Database.RemoveStickyFromDesktop(note.Id);
                NotifyMain();
            }
        };
        _windows[note.Id] = window;
        window.Show();
    }

    private void CloseWindow(long stickyId)
    {
        // 先に一覧から外しておくと、Closed で二重に片付けない
        if (_windows.Remove(stickyId, out var window))
            window.Close();
    }

    /// <summary>モニターを外したなどで、保存した場所が画面の外になっていたら、右上に戻す。</summary>
    private static DesktopSticky KeepOnScreen(DesktopSticky placement)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        // 上の帯（つかんで動かす所）が少しでも見えていればよい
        if (screen.IntersectsWith(new Rect(placement.X, placement.Y, placement.Width, 32)))
            return placement;
        var area = SystemParameters.WorkArea;
        return placement with { X = area.Right - placement.Width - 24, Y = area.Top + 24 };
    }

    // 付箋画面のボタンの見た目などを合わせる
    private static void NotifyMain()
    {
        if (Application.Current.MainWindow is MainWindow main)
            main.DataChanged();
    }
}
