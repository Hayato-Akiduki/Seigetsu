using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Seigetsu.Data;
using Seigetsu.Services;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace Seigetsu;

/// <summary>
/// デスクトップに出した付箋 1 枚。霽月の窓を閉じて（タスクトレイにしまって）いても出たまま。
/// ふだんはほかの窓のうしろ（壁紙のすぐ上）にいて、作業のじゃまをしない。ピンを押すと最前面に固定する。
/// 開け閉めは DesktopStickies がまとめて受け持つ。
/// </summary>
public partial class DesktopStickyWindow : Window
{
    // 動かしたり大きさを変えたりし終わってから、位置を保存する
    private readonly DispatcherTimer _saveDelay = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private bool _ready;

    public DesktopStickyWindow(StickyNote note, DesktopSticky placement)
    {
        InitializeComponent();
        Note = note;
        Left = placement.X;
        Top = placement.Y;
        Width = placement.Width;
        Height = placement.Height;
        Topmost = placement.Topmost;
        Update(note);
        UpdatePin();
        _saveDelay.Tick += (_, _) => SaveBounds();
        // 最初に置いたときの動きは保存しない
        ContentRendered += (_, _) => _ready = true;
        // クリックしたり編集を終えたりしたあとも、うしろに戻す
        Deactivated += (_, _) => SendToBack();
        Closed += (_, _) =>
        {
            if (_saveDelay.IsEnabled)
                SaveBounds();
        };
    }

    public StickyNote Note { get; private set; }

    /// <summary>付箋の中身や外観が変わったら呼ぶ。</summary>
    public void Update(StickyNote note)
    {
        Note = note;
        Title = $"付箋「{note.Title}」";
        TitleText.Text = note.Title;
        BodyText.Text = note.Body;
        Root.Background = ThemeService.StickyBrush(note);
        Root.BorderBrush = ThemeService.Brush("LineBrush");
    }

    private void UpdatePin()
    {
        PinIcon.Symbol = Topmost ? SymbolRegular.Pin24 : SymbolRegular.PinOff24;
        PinIcon.Filled = Topmost;
        var label = Topmost ? "いちばん手前に固定しています（押すとほかの窓のうしろに戻す）" : "いちばん手前に固定する";
        PinButton.ToolTip = label;
        AutomationProperties.SetName(PinButton, label);
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            OnEditClick(sender, e);
            return;
        }
        DragMove();
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        App.Database.SetDesktopStickyTopmost(Note.Id, Topmost);
        UpdatePin();
        SendToBack();
    }

    private void OnEditClick(object sender, RoutedEventArgs e)
    {
        // 霽月の窓がタスクトレイにしまわれていても出せるよう、この付箋の窓の上に出す
        if (Application.Current.MainWindow is MainWindow main)
            main.OpenStickyDialog(Note, owner: this);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => App.DesktopStickies.Hide(Note.Id);

    private void OnBoundsChanged(object? sender, EventArgs e)
    {
        if (!_ready)
            return;
        _saveDelay.Stop();
        _saveDelay.Start();
    }

    private void SaveBounds()
    {
        _saveDelay.Stop();
        if (WindowState == WindowState.Normal)
            App.Database.SaveDesktopStickyBounds(Note.Id, Left, Top, Width, Height);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        // Windows 11 では角を丸くする（それより前の Windows では何も起きない）
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));

        // デスクトップ（Progman）の持ち物にしておくと、「デスクトップを表示」（Win+D）でも消えない
        var desktop = FindWindow("Progman", null);
        if (desktop != IntPtr.Zero)
            SetWindowLongPtr(handle, GWLP_HWNDPARENT, desktop);

        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        Loaded += (_, _) => SendToBack();
    }

    /// <summary>最前面に固定していなければ、ほかのすべての窓のうしろ（壁紙のすぐ上）に置く。</summary>
    private void SendToBack()
    {
        if (Topmost || OwnedWindows.Count > 0)
            return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
            SetWindowPos(handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // クリックなどで手前に出ようとしても、いちばんうしろのままにする。
    // 編集の窓を出しているあいだは、それが隠れないようにそのままにする
    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_WINDOWPOSCHANGING && !Topmost && OwnedWindows.Count == 0)
        {
            var position = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            if ((position.flags & SWP_NOZORDER) == 0)
            {
                position.hwndInsertAfter = HWND_BOTTOM;
                Marshal.StructureToPtr(position, lParam, false);
            }
        }
        return IntPtr.Zero;
    }

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int GWLP_HWNDPARENT = -8;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
}
