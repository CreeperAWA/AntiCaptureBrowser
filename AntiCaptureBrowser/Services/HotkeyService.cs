using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AntiCaptureBrowser.Services;

/// <summary>
/// 基于 RegisterHotKey 的全局热键服务：即使窗口被隐藏（或应用未获得焦点）也能触发。
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 0xB02C;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>全局热键被按下（隐藏 ⇆ 恢复 切换）。</summary>
    public event Action? Pressed;

    private IntPtr _hwnd;
    private bool _hooked;
    private bool _registered;

    public bool TryRegister(Window window, string combo)
    {
        Unregister();
        if (!HotkeyParser.TryParse(combo, out uint modifiers, out uint vk)) return false;

        _hwnd = new WindowInteropHelper(window).EnsureHandle();
        if (!_hooked)
        {
            var source = HwndSource.FromHwnd(_hwnd);
            source?.AddHook(WndProcHook);
            _hooked = source is not null;
        }

        _registered = RegisterHotKey(_hwnd, HotkeyId, modifiers, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _hwnd != IntPtr.Zero)
        {
            UnregisterHotKey(_hwnd, HotkeyId);
        }
        _registered = false;
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            handled = true;
            Pressed?.Invoke();
        }
        return IntPtr.Zero;
    }

    public void Dispose() => Unregister();
}
