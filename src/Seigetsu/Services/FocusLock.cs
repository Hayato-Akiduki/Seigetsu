using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Seigetsu.Data;

namespace Seigetsu.Services;

/// <summary>
/// 集中ロック。集中タイマーが動いているあいだ、霽月の画面ですべてのモニターを覆い、
/// Alt+Tab や Windows キーなど、ほかのアプリに移るキー操作を止める。
/// Ctrl+Alt+Del は Windows が止めさせないので、そこからは必ず抜け出せる。
/// </summary>
public sealed class FocusLock
{
    private readonly List<FocusLockWindow> _windows = [];
    private IntPtr _hook;
    private HookProc? _hookProc; // 消されないよう持っておく（Windows から呼ばれる）

    /// <summary>ロック画面の「長押しで一時停止」を押し終えたとき。</summary>
    public event Action? StopRequested;

    public bool Active => _windows.Count > 0;

    /// <summary>設定で集中ロックを使うことにしているか。</summary>
    public static bool Enabled
    {
        get => App.Database.GetSetting(SettingKeys.FocusLock, "0") == "1";
        set => App.Database.SetSetting(SettingKeys.FocusLock, value ? "1" : "0");
    }

    /// <summary>
    /// 今ロックするべきか。ストップウォッチと、ポモドーロの作業時間が動いているときだけ。
    /// 休憩中と、料理などにも使う「タイマー」ではロックしない。
    /// </summary>
    public static bool ShouldLock(FocusTimer timer) =>
        Enabled && timer.Running
        && (timer.Mode == TimerMode.Stopwatch || (timer.IsPomodoro && timer.Phase == TimerPhase.Work));

    /// <summary>タイマーの状態に合わせて、ロックをかける・解く。0.5 秒ごとに呼ばれる。</summary>
    public void Update(FocusTimer timer, Window main)
    {
        var should = ShouldLock(timer);
        if (should && !Active)
            Start(main);
        else if (!should && Active)
            Stop(main);
        else if (Active)
            Refresh();
    }

    private void Start(Window main)
    {
        // 霽月の窓がある画面に時間を出す（タスクトレイに隠れているときは、いちばん主な画面）
        var handle = main.IsVisible ? new WindowInteropHelper(main).Handle : IntPtr.Zero;
        var mainScreen = handle != IntPtr.Zero
            ? System.Windows.Forms.Screen.FromHandle(handle)
            : System.Windows.Forms.Screen.PrimaryScreen;
        var screens = System.Windows.Forms.Screen.AllScreens;
        var mainIndex = Math.Max(0, Array.FindIndex(screens, s => s.DeviceName == mainScreen?.DeviceName));
        for (var i = 0; i < screens.Length; i++)
        {
            var window = new FocusLockWindow(screens[i].Bounds, i == mainIndex);
            window.StopRequested += () => StopRequested?.Invoke();
            _windows.Add(window);
        }
        // ほかの画面を先に出し、時間を出す窓を最後に出して手前にする
        foreach (var window in _windows.OrderBy(w => w.IsMain))
            window.Show();

        InstallHook();
        // ロックしているあいだは、画面が暗くなったりスリープしたりしないようにする
        SetThreadExecutionState(EsContinuous | EsDisplayRequired);
    }

    private void Stop(Window main)
    {
        RemoveHook();
        foreach (var window in _windows)
            window.CloseForReal();
        _windows.Clear();
        SetThreadExecutionState(EsContinuous);
        if (main.IsVisible)
            main.Activate();
    }

    /// <summary>アプリを終えるときに、必ず解く。</summary>
    public void Release(Window main)
    {
        if (Active)
            Stop(main);
    }

    private void Refresh()
    {
        FocusLockWindow? front = null;
        foreach (var window in _windows)
        {
            window.Refresh();
            if (window.IsMain)
                front = window;
        }
        // ほかのアプリが手前に出ていたら、取り戻す（Deactivated で取り逃がしたときのため）
        if (front is not null && !IsOurs(GetForegroundWindow()))
            front.BringToFront();
    }

    private static bool IsOurs(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return false;
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == Environment.ProcessId;
    }

    // ---- キー操作を止める -------------------------------------------------
    private void InstallHook()
    {
        if (_hook != IntPtr.Zero)
            return;
        _hookProc = OnKey;
        _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
    }

    private void RemoveHook()
    {
        if (_hook == IntPtr.Zero)
            return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        _hookProc = null;
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && IsBlocked(Marshal.PtrToStructure<KeyInfo>(lParam)))
            return 1; // このキーは、なかったことにする
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private static bool IsBlocked(KeyInfo key)
    {
        var alt = (key.Flags & LlkhfAltDown) != 0;
        var ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        return key.VkCode switch
        {
            VkLWin or VkRWin => true,                 // スタートメニュー、Win+Tab、Win+D など
            VkTab => alt,                             // Alt+Tab
            VkEscape => alt || ctrl,                  // Alt+Esc、Ctrl+Esc、Ctrl+Shift+Esc（タスクマネージャー）
            VkF4 => alt,                              // Alt+F4
            VkSpace => alt,                           // Alt+Space（窓のメニュー）
            _ => false,
        };
    }

    private const int WhKeyboardLl = 13;
    private const uint LlkhfAltDown = 0x20;
    private const uint VkTab = 0x09, VkEscape = 0x1B, VkSpace = 0x20, VkF4 = 0x73;
    private const uint VkLWin = 0x5B, VkRWin = 0x5C;
    private const int VkControl = 0x11;

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyInfo
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    private const uint EsContinuous = 0x80000000;
    private const uint EsDisplayRequired = 0x00000002;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
