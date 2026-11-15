using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AntiCaptureBrowser.Services;

/// <summary>
/// 应用内快捷键钩子（WH_KEYBOARD_LL）：
/// 窗口式 WebView2 下网页获得焦点时 WPF 收不到键盘事件，
/// 通过低级键盘钩子在本进程窗口处于前台时拦截宿主级组合键
/// （Ctrl+T / Ctrl+W / Ctrl+L / Ctrl+K / Ctrl+Tab）。
/// 其他按键一律放行，不影响网页与输入框的正常使用。
/// </summary>
public sealed class AppKeyboardHook : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeydown = 0x0100;
    private const int WmSyskeydown = 0x0104;
    private const int VkTab = 0x09;
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLLHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    private IntPtr _hook;
    private readonly LowLevelKeyboardProc _hookProc; // 必须持有委托引用，否则被 GC 回收后回调会导致 ExecutionEngineException
    private IntPtr _mainHwnd;

    /// <summary>宿主快捷键（"newtab" / "closetab" / "address" / "cycle" / "cycleback"），在 UI 线程触发。</summary>
    public event Action<string>? ShortcutPressed;

    public AppKeyboardHook() => _hookProc = HookProc;

    public void Install(Window window)
    {
        if (_hook != IntPtr.Zero) return;
        _mainHwnd = new WindowInteropHelper(window).EnsureHandle();
        _hook = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WmKeydown || wParam == (IntPtr)WmSyskeydown))
        {
            var vk = Marshal.PtrToStructure<KbdLLHookStruct>(lParam).VkCode;
            if (IsOurAppForeground() && TryMatch(vk, out string action))
            {
                // 低级钩子必须尽快返回：转交 UI 线程处理，吞掉按键不让它进入消息队列
                Application.Current?.Dispatcher.BeginInvoke(
                    DispatcherPriority.Input, () => ShortcutPressed?.Invoke(action));
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private bool IsOurAppForeground()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        GetWindowThreadProcessId(fg, out uint pid);
        return pid == Environment.ProcessId;
    }

    private bool TryMatch(uint vk, out string action)
    {
        action = "";
        bool ctrl = (GetAsyncKeyState(VkControl) & 0x8000) != 0;
        bool alt = (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(VkShift) & 0x8000) != 0;
        if (!ctrl || alt) return false;

        switch (vk)
        {
            case 'T': action = "newtab"; return true;
            case 'W': action = "closetab"; return true;
            case 'L':
            case 'K': action = "address"; return true;
            case VkTab: action = shift ? "cycleback" : "cycle"; return true;
            default: return false;
        }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
