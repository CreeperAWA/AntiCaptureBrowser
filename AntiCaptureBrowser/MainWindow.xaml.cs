using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AntiCaptureBrowser.Models;
using AntiCaptureBrowser.Services;
using Microsoft.Web.WebView2.Core;

namespace AntiCaptureBrowser;

public partial class MainWindow : Window
{
    public ObservableCollection<BrowserTab> Tabs { get; } = [];

    private BrowserTab? _selectedTab;
    private CoreWebView2Environment? _environment;
    private readonly HotkeyService _hotkeyService = new();
    private bool _isHiddenByHotkey;
    private bool _mediaSilencedByApp;

    public MainWindow()
    {
        InitializeComponent();
        TabsList.ItemsSource = Tabs;
        StateChanged += OnStateChanged;
        Closing += (_, _) =>
        {
            _hotkeyService.Dispose();
            ThemeManager.ThemeChanged -= ApplyThemeToTabs;
        };
        Loaded += async (_, _) => await OpenHomeTabAsync();
        // 系统主题切换时同步所有已打开标签页的配色
        ThemeManager.ThemeChanged += ApplyThemeToTabs;
        // 标签页数量或窗口尺寸变化时重算宽度
        Tabs.CollectionChanged += (_, _) => UpdateTabWidths();
        TabScroll.SizeChanged += (_, _) => UpdateTabWidths();
    }

    private void ApplyThemeToTabs()
    {
        foreach (var tab in Tabs)
        {
            var core = tab.WebView?.CoreWebView2;
            if (core is not null) core.Profile.PreferredColorScheme = ThemeManager.WebViewColorScheme;
        }
    }

    // ==================== 初始化 ====================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyCaptureMode();
        RegisterGlobalHotkey();
        EnableRoundedCorners();
    }

    // Win11 圆角（DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2）；旧系统自动忽略
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private void EnableRoundedCorners()
    {
        try
        {
            int preference = 2;
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
        }
        catch
        {
            // Windows 10 无此属性，忽略
        }
    }

    // ==================== 自定义标题栏窗口按钮 ====================

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnMaximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        // 最大化/还原时切换按钮图标与提示
        bool maximized = WindowState == WindowState.Maximized;
        BtnMaximize.Content = maximized ? '\uE923' : '\uE922'; // ChromeRestore / ChromeMaximize
        ToolTipService.SetToolTip(BtnMaximize, maximized ? "还原" : "最大化");
    }

    private async Task EnsureEnvironmentAsync()
    {
        if (_environment is not null) return;
        // 浏览数据/缓存存放在 exe 所在目录的 Data\WebView2 中
        var dataDir = Path.Combine(AppContext.BaseDirectory, "Data", "WebView2");
        Directory.CreateDirectory(dataDir);
        _environment = await CoreWebView2Environment.CreateAsync(null, dataDir);
    }

    private async Task OpenHomeTabAsync()
    {
        await EnsureEnvironmentAsync();
        var tab = CreateWebTab();
        await tab.InitializeAsync(_environment!, GetHomePageUrl());
    }

    private BrowserTab CreateWebTab()
    {
        var tab = new BrowserTab(isSettingsTab: false);
        HookTabEvents(tab);
        Tabs.Add(tab);
        SelectTab(tab);
        return tab;
    }

    private void HookTabEvents(BrowserTab tab)
    {
        tab.NewTabRequested += OnTabNewTabRequested;
        tab.CloseRequested += OnTabCloseRequested;
        tab.FocusAddressRequested += OnFocusAddressRequested;
        tab.CycleTabRequested += (_, backward) => CycleTab(backward);
        tab.AddressChanged += OnSelectedTabAddressChanged;
        tab.HistoryChanged += OnSelectedTabHistoryChanged;
        tab.PropertyChanged += OnTabPropertyChanged;
    }

    // ==================== 标签页管理 ====================

    private async void OnTabNewTabRequested(object? sender, string? url)
    {
        await EnsureEnvironmentAsync();
        var tab = CreateWebTab();
        await tab.InitializeAsync(_environment!, url ?? GetHomePageUrl());
    }

    private void OnTabCloseRequested(object? sender, EventArgs e)
    {
        if (sender is not BrowserTab tab) return;
        int index = Tabs.IndexOf(tab);
        Tabs.Remove(tab);

        if (Tabs.Count == 0)
        {
            Close();
            return;
        }
        if (_selectedTab == tab)
        {
            SelectTab(Tabs[Math.Min(index, Tabs.Count - 1)]);
        }
    }

    private void SelectTab(BrowserTab tab)
    {
        foreach (var t in Tabs) t.IsSelected = ReferenceEquals(t, tab);
        _selectedTab = tab;
        TabContent.Content = tab.Host;
        UpdateAddressBar();
        UpdateNavButtons();
        UpdateWindowTitle();
    }

    private void CycleTab(bool backward)
    {
        if (Tabs.Count == 0) return;
        int index = _selectedTab is null ? 0 : Tabs.IndexOf(_selectedTab);
        index = (index + (backward ? -1 : 1) + Tabs.Count) % Tabs.Count;
        SelectTab(Tabs[index]);
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BrowserTab.Title) && sender == _selectedTab)
        {
            UpdateWindowTitle();
        }
    }

    private void UpdateWindowTitle()
    {
        Title = _selectedTab is null
            ? "AntiCaptureBrowser"
            : $"{_selectedTab.Title} - AntiCaptureBrowser";
    }

    // ==================== 地址栏与导航 ====================

    private string GetHomePageUrl()
    {
        if (App.Settings.HomePageMode == HomePageMode.Blank) return "about:blank";
        return string.IsNullOrWhiteSpace(App.Settings.HomePageUrl) ? "about:blank" : App.Settings.HomePageUrl.Trim();
    }

    /// <summary>地址栏输入 → 目标 URL：绝对 URI 直接用；疑似域名补 https://；否则搜索。</summary>
    private string ResolveInputToUrl(string input)
    {
        input = input.Trim();
        if (input.Length == 0) return GetHomePageUrl();

        if (Uri.TryCreate(input, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "file" or "about")
        {
            return uri.ToString();
        }

        bool looksLikeHost = !input.Contains(' ')
            && (input.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                || (input.Contains('.') && Uri.CheckHostName(input.TrimEnd('/')) != UriHostNameType.Unknown));
        if (looksLikeHost && Uri.TryCreate("https://" + input, UriKind.Absolute, out var withScheme))
        {
            return withScheme.ToString();
        }

        string template = SearchEngines.ResolveTemplate(App.Settings.SearchEngineId, App.Settings.CustomSearchUrl);
        return template
            .Replace("{query}", Uri.EscapeDataString(input))
            .Replace("%s", Uri.EscapeDataString(input));
    }

    private void NavigateFromAddress(string input)
    {
        var tab = _selectedTab;
        if (tab is not { IsWebViewReady: true }) return;
        tab.Navigate(ResolveInputToUrl(input));
        tab.FocusContent();
    }

    private void UpdateAddressBar()
    {
        AddressBox.Text = _selectedTab?.CurrentUrl ?? "";
    }

    private void OnSelectedTabAddressChanged(object? sender, string url)
    {
        if (ReferenceEquals(sender, _selectedTab)) AddressBox.Text = url;
    }

    private void OnFocusAddressRequested(object? sender, EventArgs e)
    {
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            NavigateFromAddress(AddressBox.Text);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            UpdateAddressBar();
            _selectedTab?.FocusContent();
        }
    }

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => AddressBox.SelectAll();

    // ==================== 工具栏按钮 ====================

    private void BtnNewTab_Click(object sender, RoutedEventArgs e) => OnTabNewTabRequested(this, null);

    private void BtnBack_Click(object sender, RoutedEventArgs e) => _selectedTab?.GoBack();
    private void BtnForward_Click(object sender, RoutedEventArgs e) => _selectedTab?.GoForward();

    private void BtnReload_Click(object sender, RoutedEventArgs e) => _selectedTab?.Reload();
    private void BtnHome_Click(object sender, RoutedEventArgs e) => _selectedTab?.Navigate(GetHomePageUrl());

    private void BtnSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void TabButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab) SelectTab(tab);
    }

    private void TabClose_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            OnTabCloseRequested(tab, EventArgs.Empty);
        }
    }

    private void OnSelectedTabHistoryChanged(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _selectedTab)) UpdateNavButtons();
    }

    private void UpdateNavButtons()
    {
        var core = _selectedTab?.WebView?.CoreWebView2;
        BtnBack.IsEnabled = core is { CanGoBack: true };
        BtnForward.IsEnabled = core is { CanGoForward: true };
    }

    // ==================== 设置页 ====================

    private void OpenSettings()
    {
        var existing = Tabs.FirstOrDefault(t => t.IsSettingsTab);
        if (existing is not null)
        {
            SelectTab(existing);
            return;
        }

        var tab = new BrowserTab(isSettingsTab: true);
        tab.CloseRequested += OnTabCloseRequested;
        var view = new Views.SettingsView();
        view.SettingsApplied += OnSettingsApplied;
        tab.SetSettingsContent(view);
        Tabs.Add(tab);
        SelectTab(tab);
    }

    private void OnSettingsApplied(object? sender, EventArgs e)
    {
        ApplyCaptureMode();
        bool hotkeyOk = RegisterGlobalHotkey();
        if (sender is Views.SettingsView view) view.SetHotkeyStatus(hotkeyOk);
    }

    // ==================== 防捕获（SetWindowDisplayAffinity） ====================

    private void ApplyCaptureMode()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        DisplayAffinity.Apply(hwnd, App.Settings.CaptureMode);
    }

    // ==================== 全局热键（隐藏/恢复） ====================

    private bool RegisterGlobalHotkey()
    {
        bool ok = _hotkeyService.TryRegister(this, App.Settings.HideHotkey);
        if (ok)
        {
            _hotkeyService.Pressed -= ToggleHide;
            _hotkeyService.Pressed += ToggleHide;
        }
        return ok;
    }

    private void ToggleHide()
    {
        if (_isHiddenByHotkey) RestoreFromHide();
        else HideNow();
    }

    private void HideNow()
    {
        SilenceMedia(true);
        _isHiddenByHotkey = true;
        Hide();
    }

    private void RestoreFromHide()
    {
        _isHiddenByHotkey = false;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // 临时置顶把窗口带到前台
        Topmost = true;
        Dispatcher.InvokeAsync(() => Topmost = false, DispatcherPriority.Background);
        SilenceMedia(false);
    }

    // ==================== 隐藏/最小化时的媒体静音与暂停 ====================

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            if (App.Settings.MuteOnHide || App.Settings.PauseOnHide) SilenceMedia(true);
        }
        else if (!_isHiddenByHotkey)
        {
            SilenceMedia(false);
        }
    }

    private void SilenceMedia(bool on)
    {
        bool shouldSilence = on && (App.Settings.MuteOnHide || App.Settings.PauseOnHide);
        if (on && !shouldSilence) return;
        if (!on && !_mediaSilencedByApp) return;

        foreach (var tab in Tabs)
        {
            if (on) tab.Silence(App.Settings.MuteOnHide, App.Settings.PauseOnHide);
            else tab.Unsilence();
        }
        _mediaSilencedByApp = on;
    }

    // ==================== 标签页宽度自适应（仿 Chrome）与拖动排序 ====================

    private const double MaxTabWidth = 170;      // 标签最大宽度
    private const double CompactThreshold = 150; // 低于此宽度：关闭按钮仅在激活/悬停的标签上显示
    private const double IconThreshold = 66;     // 低于此宽度：仅显示图标
    private const double MinTabWidth = 42;       // 图标模式最小宽度
    private const double TabStripReserve = 40;   // 标签栏中新建按钮及其边距占用的宽度

    private BrowserTab? _pendingDragTab;
    private Point _pendingDragStart;
    private BrowserTab? _dragTab;
    private Point _dragStartPos;
    private bool _isDragging;

    /// <summary>按标签数量与可用宽度计算每个标签的宽度和尺寸模式。</summary>
    private void UpdateTabWidths()
    {
        int n = Tabs.Count;
        if (n == 0 || TabScroll.ViewportWidth <= 0) return;

        double avail = TabScroll.ViewportWidth - TabStripReserve - n * 2; // 减去新建按钮与每个标签的右外边距
        double w = Math.Clamp(avail / n, MinTabWidth, MaxTabWidth);
        var mode = w >= CompactThreshold ? TabSizingMode.Full
                 : w >= IconThreshold ? TabSizingMode.Compact
                 : TabSizingMode.Icon;
        w = Math.Round(w);

        foreach (var tab in Tabs)
        {
            tab.TabWidthMode = mode;
            tab.TabWidth = w;
        }
    }

    private void TabButton_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (sender is not FrameworkElement { DataContext: BrowserTab tab }) return;
        _pendingDragTab = tab;
        _pendingDragStart = e.GetPosition(TabScroll);
    }

    // 中键点击标签页直接关闭（Chrome 习惯）
    private void TabButton_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: BrowserTab tab })
        {
            OnTabCloseRequested(tab, EventArgs.Empty);
        }
    }

    private void TabStrip_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        // 按下并移动超过阈值 → 开始拖动（捕获到稳定的 TabScroll 上，重排时不丢捕获）
        if (!_isDragging && _pendingDragTab is not null && e.MouseDevice.LeftButton == MouseButtonState.Pressed)
        {
            var pos = e.GetPosition(TabScroll);
            if (Math.Abs(pos.X - _pendingDragStart.X) > 6 || Math.Abs(pos.Y - _pendingDragStart.Y) > 6)
            {
                _dragTab = _pendingDragTab;
                _pendingDragTab = null;
                _isDragging = true;
                TabScroll.CaptureMouse();
            }
        }

        if (!_isDragging || _dragTab is null) return;
        int n = Tabs.Count;
        if (n < 2) return;

        // 光标落在第几个标签区间上 → 实时移动到该位置
        var p = e.GetPosition(TabsList);
        double unit = _dragTab.TabWidth + 2; // 含右外边距
        int target = Math.Clamp((int)(p.X / unit), 0, n - 1);
        int current = Tabs.IndexOf(_dragTab);
        if (target != current)
        {
            Tabs.Move(current, target);
        }
    }

    private void TabStrip_PreviewMouseUp(object sender, MouseButtonEventArgs e) => EndTabDrag();

    private void TabScroll_LostMouseCapture(object sender, MouseEventArgs e) => EndTabDrag();

    private void EndTabDrag()
    {
        _pendingDragTab = null;
        _dragTab = null;
        if (_isDragging)
        {
            _isDragging = false;
            if (TabScroll.IsMouseCaptured) TabScroll.ReleaseMouseCapture();
        }
    }

    // ==================== 窗口级快捷键（焦点不在网页内时） ====================

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        if (mods == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.T:
                    e.Handled = true;
                    OnTabNewTabRequested(this, null);
                    break;
                case Key.W:
                case Key.F4:
                    e.Handled = true;
                    if (_selectedTab is not null) OnTabCloseRequested(_selectedTab, EventArgs.Empty);
                    break;
                case Key.L:
                case Key.K:
                    e.Handled = true;
                    OnFocusAddressRequested(this, EventArgs.Empty);
                    break;
                case Key.Tab:
                    e.Handled = true;
                    CycleTab(false);
                    break;
            }
        }
        else if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Tab)
        {
            e.Handled = true;
            CycleTab(true);
        }
        else if (mods == ModifierKeys.Alt && e.Key == Key.Home)
        {
            e.Handled = true;
            _selectedTab?.Navigate(GetHomePageUrl());
        }
        else if (e.Key == Key.F6)
        {
            e.Handled = true;
            OnFocusAddressRequested(this, EventArgs.Empty);
        }
    }
}
