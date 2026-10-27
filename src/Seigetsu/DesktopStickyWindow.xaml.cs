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
        Root.Background = ThemeService.StickyBrush(note.Id);
        Root.BorderBrush = ThemeService.Brush("LineBrush");
    }

    private void UpdatePin()
    {
        PinIcon.Symbol = Topmost ? SymbolRegular.Pin24 : SymbolRegular.PinOff24;
        PinIcon.Filled = Topmost;
        var label = Topmost ? "いちばん手前に固定しています（押すと解除）" : "いちばん手前に固定する";
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

    // Windows 11 では角を丸くする（それより前の Windows では何も起きない）
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
}
