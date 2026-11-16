using System.Runtime.InteropServices;
using System.Windows.Threading;
using AntiCaptureBrowser.Models;

namespace AntiCaptureBrowser.Services;

/// <summary>
/// 捕获保护监视器：主窗口之外的附属窗口（WebView2 的 DevTools 窗口、
/// 独立下载窗口等）不会自动继承主窗口的 SetWindowDisplayAffinity，
/// 此处通过 EVENT_OBJECT_SHOW 钩子 + 定时扫描，对它们应用相同的保护，
/// 保护关闭时恢复为可捕获。
/// </summary>
public sealed class WindowAffinityWatcher : IDisposable
{
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint threadId, uint dwmsEventTime);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private const uint EventObjectShow = 0x8002;
    private const uint WineventOutofcontext = 0;
    private const uint GaRoot = 2;
    private const uint GwOwner = 4;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hModWinEventProc, WinEventDelegate pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint nCmd);

    private IntPtr _mainHwnd;
    private IntPtr _winEventHook;
    private readonly DispatcherTimer _sweepTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly WinEventDelegate _winEventDelegate; // 持有委托防止 GC 回收

    public WindowAffinityWatcher() => _winEventDelegate = OnWinEvent;

    /// <summary>开始监视：新出现的附属窗口立即应用保护，并每 2 秒兜底扫描一次。</summary>
    public void Start(IntPtr mainHwnd)
    {
        _mainHwnd = mainHwnd;
        if (_winEventHook == IntPtr.Zero)
        {
            _winEventHook = SetWinEventHook(EventObjectShow, EventObjectShow, IntPtr.Zero, _winEventDelegate, 0, 0, WineventOutofcontext);
        }
        if (!_sweepTimer.IsEnabled)
        {
            _sweepTimer.Tick += (_, _) => Sweep();
            _sweepTimer.Start();
        }
        Sweep();
    }

    /// <summary>停止监视，并将所有附属窗口恢复为可捕获。</summary>
    public void Stop()
    {
        if (_winEventHook != IntPtr.Zero)
        {
            UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
        _sweepTimer.Stop();
        Sweep(CaptureMode.Capturable);
    }

    private void OnWinEvent(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint threadId, uint dwmsEventTime)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) return; // OBJID_WINDOW
        if (IsVisibleTopLevelOwnedByMain(hwnd))
        {
            DisplayAffinity.Apply(hwnd, App.Settings.CaptureMode);
        }
    }

    private void Sweep(CaptureMode? mode = null)
    {
        var current = mode ?? App.Settings.CaptureMode;
        EnumWindows((hwnd, _) =>
        {
            if (hwnd != _mainHwnd && IsWindowVisible(hwnd) && IsOwnedByMain(hwnd))
            {
                DisplayAffinity.Apply(hwnd, current);
            }
            return true;
        }, IntPtr.Zero);
    }

    /// <summary>窗口自身为主窗口，或其所有者链包含主窗口。</summary>
    private bool IsOwnedByMain(IntPtr hwnd)
    {
        if (hwnd == _mainHwnd) return true;
        var current = hwnd;
        for (int depth = 0; depth < 16 && current != IntPtr.Zero; depth++)
        {
            current = GetWindow(current, GwOwner);
            if (current == _mainHwnd) return true;
        }
        return false;
    }

    private bool IsVisibleTopLevelOwnedByMain(IntPtr hwnd)
        => IsWindowVisible(hwnd)
           && GetAncestor(hwnd, GaRoot) == hwnd
           && IsOwnedByMain(hwnd);

    public void Dispose() => Stop();
}
