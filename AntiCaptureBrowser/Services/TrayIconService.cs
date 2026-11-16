using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace AntiCaptureBrowser.Services;

/// <summary>托盘图标：Shell_NotifyIcon 纯 P/Invoke 实现（不依赖 WinForms）。</summary>
public sealed class TrayIconService : IDisposable
{
    private const int NimAdd = 0x00;
    private const int NimModify = 0x01;
    private const int NimDelete = 0x02;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const int WmTrayCallback = 0x8123;    // 自定义回调消息
    private const int WmLbuttonDblClk = 0x0203;
    private const int WmRbuttonUp = 0x0205;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public int CbSize;
        public IntPtr HWnd;
        public uint UId;
        public uint UFlags;
        public uint UCallbackMessage;
        public IntPtr HIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SzTip;
        public uint DwState;
        public uint DwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string SzInfo;
        public uint UVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string SzInfoTitle;
        public uint DwInfoFlags;
        public Guid GuidItem;
        public IntPtr HBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NotifyIconData lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconFromResourceEx(byte[] pbIconBits, uint cbIconBits, bool fIcon, uint dwVersion, int cxDesired, int cyDesired, uint Flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>托盘图标被双击（恢复窗口）。</summary>
    public event Action? Activated;

    /// <summary>托盘图标被右键单击（请求弹出菜单）。</summary>
    public event Action? MenuRequested;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _added;

    /// <summary>窗口过程转发：处理托盘回调消息，返回是否已处理。</summary>
    public bool HandleCallback(int msg, IntPtr lParam)
    {
        if (msg != WmTrayCallback) return false;
        switch ((int)lParam)
        {
            case WmLbuttonDblClk:
                Activated?.Invoke();
                break;
            case WmRbuttonUp:
                MenuRequested?.Invoke();
                break;
        }
        return true;
    }

    /// <summary>显示或更新托盘图标（重复调用等价于修改）。</summary>
    public void Show(IntPtr hwnd, BitmapSource icon, string tooltip)
    {
        _hwnd = hwnd;
        DestroyCurrentIcon();
        _hIcon = CreateIconFromPng(icon);
        var data = CreateData(_hIcon, tooltip);
        _added = Shell_NotifyIcon(NimAdd, ref data) || (Shell_NotifyIcon(NimModify, ref data) && _added);
    }

    /// <summary>仅更新图标与提示（未显示则改为显示）。</summary>
    public void Update(BitmapSource icon, string tooltip) => Show(_hwnd, icon, tooltip);

    /// <summary>隐藏托盘图标。</summary>
    public void Hide()
    {
        if (!_added) return;
        var data = CreateData(IntPtr.Zero, "");
        Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
        DestroyCurrentIcon();
    }

    private NotifyIconData CreateData(IntPtr hIcon, string tip) => new()
    {
        CbSize = Marshal.SizeOf<NotifyIconData>(),
        HWnd = _hwnd,
        UId = 1,
        UFlags = NifMessage | NifIcon | NifTip,
        UCallbackMessage = WmTrayCallback,
        HIcon = hIcon,
        SzTip = tip.Length > 127 ? tip[..127] : tip,
    };

    private void DestroyCurrentIcon()
    {
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    /// <summary>PNG 字节直接构建 HICON（Vista+ 支持 PNG 资源）。</summary>
    private static IntPtr CreateIconFromPng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        byte[] bytes = stream.ToArray();
        return CreateIconFromResourceEx(bytes, (uint)bytes.Length, true, 0x300, 32, 32, 0);
    }

    public void Dispose()
    {
        Hide();
    }
}
