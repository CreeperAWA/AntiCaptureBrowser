using System.Runtime.InteropServices;
using AntiCaptureBrowser.Models;

namespace AntiCaptureBrowser.Services;

/// <summary>
/// 通过 Win32 SetWindowDisplayAffinity 控制窗口是否可被屏幕捕获：
///   WDA_NONE               —— 正常可捕获；
///   WDA_MONITOR            —— 捕获结果为黑色；
///   WDA_EXCLUDEFROMCAPTURE —— 窗口从捕获中完全排除（截图/录屏/共享里“透明”不可见）。
/// </summary>
public static class DisplayAffinity
{
    private const uint WDA_NONE = 0x00000000;
    private const uint WDA_MONITOR = 0x00000001;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    public static bool Apply(IntPtr hwnd, CaptureMode mode)
    {
        if (hwnd == IntPtr.Zero) return false;
        uint affinity = mode switch
        {
            CaptureMode.Black => WDA_MONITOR,
            CaptureMode.Transparent => WDA_EXCLUDEFROMCAPTURE,
            _ => WDA_NONE,
        };
        return SetWindowDisplayAffinity(hwnd, affinity);
    }
}
