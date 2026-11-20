using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AntiCaptureBrowser.Services;
using Microsoft.Web.WebView2.Core;

namespace AntiCaptureBrowser;

/// <summary>标签页尺寸模式（随标签数量自适应，仿 Chrome）。</summary>
public enum TabSizingMode
{
    /// <summary>完整：标题 + 常显关闭按钮。</summary>
    Full,
    /// <summary>紧凑：标题，关闭按钮仅在激活或悬停的标签上显示。</summary>
    Compact,
    /// <summary>仅图标：悬停时图标临时替换为关闭按钮。</summary>
    Icon,
}

/// <summary>
/// 一个浏览器标签页：普通标签页内含 WebView2；设置标签页承载 SettingsView。
/// </summary>
public class BrowserTab : INotifyPropertyChanged
{
    public bool IsSettingsTab { get; }

    /// <summary>标签页内容宿主，直接放入主窗口的 ContentControl。</summary>
    public Grid Host { get; }

    /// <summary>窗口式 WebView2 宿主（原生子窗口直渲，视频/滚动性能最佳）。</summary>
    public Microsoft.Web.WebView2.Wpf.WebView2? WebView { get; private set; }

    public bool IsWebViewReady { get; private set; }

    private string _title = "新标签页";
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; OnPropertyChanged(); } }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
    }

    private double _tabWidth = 170;
    /// <summary>标签页当前宽度（由主窗口按标签数量自适应计算）。</summary>
    public double TabWidth
    {
        get => _tabWidth;
        set { if (Math.Abs(_tabWidth - value) > 0.5) { _tabWidth = value; OnPropertyChanged(); } }
    }

    private TabSizingMode _tabWidthMode = TabSizingMode.Full;
    /// <summary>当前尺寸模式（控制关闭按钮/标题/图标的显示策略）。</summary>
    public TabSizingMode TabWidthMode
    {
        get => _tabWidthMode;
        set { if (_tabWidthMode != value) { _tabWidthMode = value; OnPropertyChanged(); } }
    }

    /// <summary>固定（Pin）：始终仅图标模式、固定最小宽度。</summary>
    public bool Pinned { get; set; }

    /// <summary>最后激活时间（选中或在页面内交互时刷新），用于标签页停放判定。</summary>
    public DateTime LastActiveTime { get; set; } = DateTime.Now;

    /// <summary>是否已停放（TrySuspend 释放内存）。</summary>
    public bool IsParked { get; private set; }

    /// <summary>历史记录标签页标识。</summary>
    public bool IsHistoryTab { get; }

    private ImageSource? _iconSource;
    /// <summary>站点图标（favicon），由 WebView2 提供；无图标时模板回退到地球标识。</summary>
    public ImageSource? IconSource
    {
        get => _iconSource;
        set
        {
            if (!ReferenceEquals(_iconSource, value))
            {
                _iconSource = value;
                OnPropertyChanged();
            }
        }
    }

    public string CurrentUrl => WebView?.CoreWebView2?.Source ?? WebView?.Source?.ToString() ?? "";

    // 由 MainWindow 订阅的交互请求
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string?>? NewTabRequested;      // null = 打开主页
    public event EventHandler? CloseRequested;
    public event EventHandler<string>? AddressChanged;
    public event EventHandler<string>? Navigated;              // 导航完成（含最终 URL）
    public event EventHandler<bool>? FullScreenChanged;        // 页面进入/退出 HTML5 全屏
    public event EventHandler? HistoryChanged;

    private const string PauseMediaScript =
        "document.querySelectorAll('video,audio').forEach(function(m){try{m.pause()}catch(e){}});";

    private bool _wasMutedBeforeSilence;

    public BrowserTab(bool isSettingsTab, bool isHistoryTab = false)
    {
        IsSettingsTab = isSettingsTab;
        IsHistoryTab = isHistoryTab;
        Host = new Grid();
        // 背景交给主窗口 / SettingsView 自身（跟随主题），宿主保持透明
        if (isSettingsTab)
        {
            Title = isHistoryTab ? "浏览历史" : "设置";
        }
    }

    public void SetSettingsContent(UIElement content) => Host.Children.Add(content);

    /// <summary>创建并初始化 WebView2，然后导航到初始地址。</summary>
    public async Task InitializeAsync(CoreWebView2Environment environment, string? initialUrl)
    {
        if (IsSettingsTab || IsWebViewReady) return;

        var webView = new Microsoft.Web.WebView2.Wpf.WebView2();
        Host.Children.Add(webView);
        WebView = webView;

        await webView.EnsureCoreWebView2Async(environment);
        var core = webView.CoreWebView2;
        IsWebViewReady = true;

        core.DocumentTitleChanged += (_, _) => Title = string.IsNullOrWhiteSpace(core.DocumentTitle)
            ? core.Source
            : core.DocumentTitle;
        core.SourceChanged += (_, _) => AddressChanged?.Invoke(this, core.Source);
        core.HistoryChanged += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);
        // 页面获得交互即视为活跃，重置停放计时
        webView.GotFocus += (_, _) => LastActiveTime = DateTime.Now;
        // 用导航生命周期驱动标题（about:blank 等页面不触发 DocumentTitleChanged）
        core.NavigationStarting += (_, _) => Title = "正在加载…";
        core.NavigationCompleted += (_, _) =>
        {
            var docTitle = core.DocumentTitle;
            Title = string.IsNullOrWhiteSpace(docTitle)
                ? (string.IsNullOrWhiteSpace(core.Source) ? "新标签页" : core.Source)
                : docTitle;
            AddressChanged?.Invoke(this, core.Source);
        };
        core.NewWindowRequested += (_, e) =>
        {
            // target=_blank / window.open 一律在新标签页中打开
            e.Handled = true;
            NewTabRequested?.Invoke(this, e.Uri);
        };
        core.WindowCloseRequested += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        // 页面 HTML5 全屏（视频全屏按钮等）时通知宿主隐藏浏览器 UI
        core.ContainsFullScreenElementChanged += (_, _)
            => FullScreenChanged?.Invoke(this, core.ContainsFullScreenElement);
        // 站点图标变化时加载真实 favicon；导航完成后兜底补抓一次
        core.FaviconChanged += async (_, _) => await LoadFaviconAsync(core);
        core.NavigationCompleted += async (_, _) =>
        {
            Navigated?.Invoke(this, core.Source);
            await Task.Delay(600);
            if (IconSource is null) await LoadFaviconAsync(core);
        };
        // 组合模式控件的键盘输入走 WPF 管线，用 PreviewKeyDown 拦截宿主级快捷键
        // （已改为窗口式宿主：宿主级快捷键由 AppKeyboardHook 低级钩子统一处理）
        core.NavigationStarting += (_, _) => LastActiveTime = DateTime.Now;

        core.Settings.AreDefaultContextMenusEnabled = true;
        ApplyUserAgent();
        // 让网页内容跟随应用主题（prefers-color-scheme）
        core.Profile.PreferredColorScheme = ThemeManager.WebViewColorScheme;

        if (!string.IsNullOrWhiteSpace(initialUrl))
        {
            Navigate(initialUrl);
        }
        webView.Focus();
    }

    public void Navigate(string url)
    {
        if (WebView is null) return;
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            WebView.Source = new Uri(url);
        }
    }

    public void Reload() => WebView?.Reload();
    public void GoBack() => WebView?.GoBack();
    public void GoForward() => WebView?.GoForward();
    public void FocusContent() => WebView?.Focus();

    /// <summary>隐藏/最小化时调用：静音（记录原状态）并可选暂停媒体。</summary>
    public void Silence(bool mute, bool pause)
    {
        var core = WebView?.CoreWebView2;
        if (core is null) return;
        if (mute)
        {
            _wasMutedBeforeSilence = core.IsMuted;
            core.IsMuted = true;
        }
        if (pause)
        {
            _ = core.ExecuteScriptAsync(PauseMediaScript);
        }
    }

    /// <summary>恢复时调用：还原用户原本的静音状态。</summary>
    public void Unsilence()
    {
        var core = WebView?.CoreWebView2;
        if (core is null) return;
        core.IsMuted = _wasMutedBeforeSilence;
    }

    /// <summary>应用设置中的 User-Agent（空字符串即恢复 WebView2 默认）。</summary>
    public void ApplyUserAgent()
    {
        var core = WebView?.CoreWebView2;
        if (core is null) return;
        core.Settings.UserAgent = App.Settings.ResolveUserAgent() ?? "";
    }

    /// <summary>
    /// 停放标签页（Memory Saver）：TrySuspendAsync 释放其内存，页面停止计时器与媒体。
    /// 激活时自动 Resume 恢复。已停放或未就绪则忽略。
    /// </summary>
    public async void Park()
    {
        var core = WebView?.CoreWebView2;
        if (IsParked || core is null || !IsWebViewReady) return;
        try
        {
            if (await core.TrySuspendAsync())
            {
                IsParked = true;
                OnPropertyChanged(nameof(IsParked));
            }
        }
        catch
        {
            // 停放失败时保持原状
        }
    }

    /// <summary>恢复已停放的标签页。</summary>
    public void Resume()
    {
        if (!IsParked) return;
        IsParked = false;
        OnPropertyChanged(nameof(IsParked));
        try
        {
            WebView?.CoreWebView2?.Resume();
        }
        catch
        {
            // 恢复失败时标签仍可正常使用
        }
        LastActiveTime = DateTime.Now;
    }

    private async Task LoadFaviconAsync(CoreWebView2 core)
    {
        try
        {
            // FaviconChanged 可能来自非 UI 线程，绑定更新必须回到 UI 线程
            if (!Application.Current.Dispatcher.CheckAccess())
            {
                await Application.Current.Dispatcher.InvokeAsync(() => LoadFaviconAsync(core));
                return;
            }
            using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
            if (stream is null)
            {
                IconSource = null;
                return;
            }
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.StreamSource = stream;
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();
            IconSource = bitmap;
        }
        catch
        {
            // 无可用图标（如 about:blank）时回退到默认标识
            IconSource = null;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
