using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntiCaptureBrowser.Services;
using Microsoft.Web.WebView2.Core;

namespace AntiCaptureBrowser;

/// <summary>
/// 一个浏览器标签页：普通标签页内含 WebView2；设置标签页承载 SettingsView。
/// </summary>
public class BrowserTab : INotifyPropertyChanged
{
    public bool IsSettingsTab { get; }

    /// <summary>标签页内容宿主，直接放入主窗口的 ContentControl。</summary>
    public Grid Host { get; }

    public Microsoft.Web.WebView2.Wpf.WebView2CompositionControl? WebView { get; private set; }

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

    public string CurrentUrl => WebView?.CoreWebView2?.Source ?? WebView?.Source?.ToString() ?? "";

    // 由 MainWindow 订阅的交互请求
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<string?>? NewTabRequested;      // null = 打开主页
    public event EventHandler? CloseRequested;
    public event EventHandler? FocusAddressRequested;
    public event EventHandler<bool>? CycleTabRequested;      // true = 向左循环
    public event EventHandler<string>? AddressChanged;
    public event EventHandler? HistoryChanged;

    private const string PauseMediaScript =
        "document.querySelectorAll('video,audio').forEach(function(m){try{m.pause()}catch(e){}});";

    private bool _wasMutedBeforeSilence;

    public BrowserTab(bool isSettingsTab)
    {
        IsSettingsTab = isSettingsTab;
        Host = new Grid();
        // 背景交给主窗口 / SettingsView 自身（跟随主题），宿主保持透明
        if (isSettingsTab)
        {
            Title = "设置";
        }
    }

    public void SetSettingsContent(UIElement content) => Host.Children.Add(content);

    /// <summary>创建并初始化 WebView2，然后导航到初始地址。</summary>
    public async Task InitializeAsync(CoreWebView2Environment environment, string? initialUrl)
    {
        if (IsSettingsTab || IsWebViewReady) return;

        var webView = new Microsoft.Web.WebView2.Wpf.WebView2CompositionControl();
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
        // 组合模式控件的键盘输入走 WPF 管线，用 PreviewKeyDown 拦截宿主级快捷键
        webView.PreviewKeyDown += OnTabPreviewKeyDown;

        core.Settings.AreDefaultContextMenusEnabled = true;
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

    // WebView2 自带加速键处理不了宿主级快捷键，在这里拦截（Handled=true 则不再传给网页）
    private void OnTabPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (!mods.HasFlag(ModifierKeys.Control)) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.T: // 新建标签页
                e.Handled = true;
                NewTabRequested?.Invoke(this, null);
                break;
            case Key.W: // 关闭标签页
            case Key.F4:
                e.Handled = true;
                CloseRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.L: // 聚焦地址栏
            case Key.K:
                e.Handled = true;
                FocusAddressRequested?.Invoke(this, EventArgs.Empty);
                break;
            case Key.Tab: // Ctrl+Tab / Ctrl+Shift+Tab 切换标签页
                e.Handled = true;
                CycleTabRequested?.Invoke(this, mods.HasFlag(ModifierKeys.Shift));
                break;
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
