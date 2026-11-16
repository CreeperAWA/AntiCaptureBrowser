using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
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
    private readonly AppKeyboardHook _keyboardHook = new();
    private readonly WindowAffinityWatcher _affinityWatcher = new();
    private bool _forceClose;
    private bool _isHiddenByHotkey;
    private bool _mediaSilencedByApp;
    private readonly string _randomTitle = GenerateRandomTitle();

    /// <summary>随机窗口标题：10 个字符，含小写字母与数字（隐私场景下不暴露浏览器身份）。</summary>
    private static string GenerateRandomTitle()
    {
        const string chars = "abcdefghijklmnopqrstuvwxyz0123456789";
        return new string(Enumerable.Range(0, 10).Select(_ => chars[Random.Shared.Next(chars.Length)]).ToArray());
    }

    public MainWindow()
    {
        InitializeComponent();
        TabsList.ItemsSource = Tabs;
        StateChanged += OnStateChanged;
        Closing += (s, e) =>
        {
            // 关闭到托盘：取消本次关闭并隐藏窗口
            if (!_forceClose && App.Settings.CloseToTray)
            {
                e.Cancel = true;
                Hide();
                UpdateTrayVisibility();
                return;
            }

            SaveWindowBounds();
            _hotkeyService.Dispose();
            _keyboardHook.Dispose();
            _affinityWatcher.Dispose();
            ThemeManager.ThemeChanged -= ApplyThemeToTabs;
        };
        Loaded += async (_, _) => await OpenHomeTabAsync();
        // 系统主题切换时同步所有已打开标签页的配色
        ThemeManager.ThemeChanged += ApplyThemeToTabs;
        // 标签页数量或窗口尺寸变化时重算宽度
        Tabs.CollectionChanged += (_, e) => UpdateTabWidths(
            e.Action is NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Remove
                or NotifyCollectionChangedAction.Reset);
        TabScroll.SizeChanged += (_, _) => UpdateTabWidths();
        _suggestTimer.Tick += (_, _) => { _suggestTimer.Stop(); _ = FetchSuggestionsAsync(); };
        StartParkingTimer();
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
        RestoreWindowBounds();
        InitTrayIcon();
        ApplyTraySettings();
        ApplyCaptureMode();
        RegisterGlobalHotkey();
        EnableRoundedCorners();
        // 窗口式宿主下网页获得焦点时 WPF 收不到键盘事件，改由低级钩子提供宿主快捷键
        _keyboardHook.ShortcutPressed += OnKeyboardShortcut;
        _keyboardHook.Install(this);
    }

    // ==================== 托盘图标（Shell_NotifyIcon + WPF 菜单） ====================

    private readonly Services.TrayIconService _trayService = new();
    private bool _trayVisible;

    private void InitTrayIcon()
    {
        _trayService.Activated += RestoreFromTray;
        _trayService.MenuRequested += ShowTrayMenu;
        var source = (HwndSource?)PresentationSource.FromVisual(this);
        source?.AddHook(TrayWindowProc);
    }

    private IntPtr TrayWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_trayService.HandleCallback(msg, lParam)) handled = true;
        return IntPtr.Zero;
    }

    /// <summary>托盘图标可见性：任务栏图标被关闭，或窗口不可见（隐藏/收进托盘）时显示。</summary>
    private void UpdateTrayVisibility()
    {
        _trayVisible = !App.Settings.TaskbarIconEnabled || Visibility != Visibility.Visible;
        if (_trayVisible)
        {
            _trayService.Show(
                new WindowInteropHelper(this).EnsureHandle(),
                (BitmapSource)Services.AppIconFactory.GetModeIcon(App.Settings.CaptureMode, 32),
                "AntiCaptureBrowser");
        }
        else
        {
            _trayService.Hide();
        }
    }

    private void UpdateTrayIconImage()
    {
        if (_trayVisible)
        {
            _trayService.Update(
                (BitmapSource)Services.AppIconFactory.GetModeIcon(App.Settings.CaptureMode, 32),
                "AntiCaptureBrowser");
        }
    }

    private void ApplyTraySettings()
    {
        // ShowInTaskbar 切换会重建窗口句柄，需重新注册热键并重设亲和性
        ShowInTaskbar = App.Settings.TaskbarIconEnabled;
        RegisterGlobalHotkey();
        ApplyCaptureMode();
        UpdateTrayVisibility();
    }

    private void ShowTrayMenu()
    {
        // 托盘菜单需要窗口短暂前台化，否则菜单无法交互
        Activate();
        var menu = new ContextMenu();
        var openItem = new MenuItem { Header = "打开主窗口" };
        openItem.Click += (_, _) => RestoreFromTray();
        menu.Items.Add(openItem);
        menu.Items.Add(new Separator());
        var exitItem = new MenuItem { Header = "退出" };
        exitItem.Click += (_, _) =>
        {
            _forceClose = true;
            Close();
        };
        menu.Items.Add(exitItem);
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        menu.IsOpen = true;
    }

    private void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        UpdateTrayVisibility();
    }

    private void RestoreWindowBounds()
    {
        var s = App.Settings;
        if (s.WindowWidth is not double w || s.WindowHeight is not double h
            || s.WindowLeft is not double l || s.WindowTop is not double t) return;

        // 虚拟屏幕范围校验：换显示器/拔掉副屏后窗口不至于丢失在屏幕外
        double vLeft = SystemParameters.VirtualScreenLeft, vTop = SystemParameters.VirtualScreenTop;
        double vRight = vLeft + SystemParameters.VirtualScreenWidth;
        double vBottom = vTop + SystemParameters.VirtualScreenHeight;
        if (l + w < vLeft + 40 || l > vRight - 40 || t + h < vTop + 40 || t > vBottom - 40) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = l;
        Top = t;
        Width = w;
        Height = h;
        if (s.WindowState == "Maximized") WindowState = WindowState.Maximized;
    }

    private void SaveWindowBounds()
    {
        var s = App.Settings;
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        s.WindowLeft = bounds.Left;
        s.WindowTop = bounds.Top;
        s.WindowWidth = bounds.Width;
        s.WindowHeight = bounds.Height;
        s.WindowState = WindowState == WindowState.Maximized ? "Maximized" : "Normal";
    }

    private void OnKeyboardShortcut(string action)
    {
        switch (action)
        {
            case "newtab":
                OnTabNewTabRequested(this, null);
                break;
            case "closetab":
                if (_selectedTab is not null) OnTabCloseRequested(_selectedTab, EventArgs.Empty);
                break;
            case "address":
                OnFocusAddressRequested(this, EventArgs.Empty);
                break;
            case "cycle":
                CycleTab(false);
                break;
            case "cycleback":
                CycleTab(true);
                break;
        }
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
        tab.AddressChanged += OnSelectedTabAddressChanged;
        tab.HistoryChanged += OnSelectedTabHistoryChanged;
        tab.Navigated += OnTabNavigated;
        tab.PropertyChanged += OnTabPropertyChanged;
    }

    // ==================== 标签页管理 ====================

    private async void OnTabNewTabRequested(object? sender, string? url) => await OpenNewTabAsync(url);

    private async Task OpenNewTabAsync(string? url)
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
        // 选中即激活：若标签已被停放（Memory Saver）则立即恢复
        tab.LastActiveTime = DateTime.Now;
        if (tab.IsParked) tab.Resume();
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
        Title = App.Settings.WindowTitleMode switch
        {
            "generic" => "AntiCaptureBrowser",
            "random" => _randomTitle,
            _ => _selectedTab is null ? "AntiCaptureBrowser" : $"{_selectedTab.Title} - AntiCaptureBrowser",
        };
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
        if (SuggestPopup.IsOpen && e.Key == Key.Down)
        {
            e.Handled = true;
            MoveSuggestSelection(1);
            return;
        }
        if (SuggestPopup.IsOpen && e.Key == Key.Up)
        {
            e.Handled = true;
            MoveSuggestSelection(-1);
            return;
        }
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            string target = SuggestPopup.IsOpen && SuggestList.SelectedItem is string selected
                ? selected
                : AddressBox.Text;
            HideSuggest();
            NavigateFromAddress(target);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideSuggest();
            UpdateAddressBar();
            _selectedTab?.FocusContent();
        }
    }

    private void MoveSuggestSelection(int delta)
    {
        int count = SuggestList.Items.Count;
        if (count == 0) return;
        int index = SuggestList.SelectedIndex + delta;
        if (index < -1) index = count - 1;
        if (index >= count) index = -1;
        SuggestList.SelectedIndex = index;
        if (SuggestList.SelectedItem is not null)
        {
            SuggestList.ScrollIntoView(SuggestList.SelectedItem);
        }
    }

    private void SuggestList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuggestList.SelectedItem is string suggestion)
        {
            _suppressSuggest = true;
            AddressBox.Text = suggestion;
            AddressBox.CaretIndex = suggestion.Length;
            _suppressSuggest = false;
        }
    }

    private void SuggestList_Click(object sender, MouseButtonEventArgs e)
    {
        if (SuggestList.SelectedItem is string suggestion)
        {
            HideSuggest();
            NavigateFromAddress(suggestion);
        }
    }

    // ==================== 搜索联想 ====================

    private readonly DispatcherTimer _suggestTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private int _suggestSeq;
    private bool _suppressSuggest;

    private void AddressBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSuggest) return;
        _suggestTimer.Stop();
        if (string.IsNullOrWhiteSpace(AddressBox.Text))
        {
            HideSuggest();
            return;
        }
        _suggestTimer.Start();
    }

    private async Task FetchSuggestionsAsync()
    {
        int seq = ++_suggestSeq;
        string text = AddressBox.Text.Trim();
        string engine = App.Settings.SearchEngineId;
        if (text.Length == 0 || engine == "custom")
        {
            HideSuggest();
            return;
        }

        var items = await SuggestionService.GetSuggestionsAsync(engine, text);
        if (seq != _suggestSeq || _isDragging) return; // 过期响应或正在拖动标签
        if (items.Length == 0)
        {
            HideSuggest();
            return;
        }

        SuggestList.ItemsSource = items;
        SuggestList.SelectedIndex = -1;
        SuggestPopup.IsOpen = true;
    }

    private void HideSuggest()
    {
        _suggestSeq++;
        _suggestTimer.Stop();
        SuggestPopup.IsOpen = false;
        SuggestList.ItemsSource = null;
    }

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => AddressBox.SelectAll();

    // Chrome 行为：点击未聚焦的地址栏时全选整个 URL（再次点击则定位光标）
    private void AddressBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!AddressBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            AddressBox.Focus();
            AddressBox.SelectAll();
        }
    }

    // ==================== 工具栏按钮 ====================

    private void BtnNewTab_Click(object sender, RoutedEventArgs e) => OnTabNewTabRequested(this, null);

    private void BtnBack_Click(object sender, RoutedEventArgs e) => _selectedTab?.GoBack();
    private void BtnForward_Click(object sender, RoutedEventArgs e) => _selectedTab?.GoForward();

    private void BtnReload_Click(object sender, RoutedEventArgs e) => _selectedTab?.Reload();
    private void BtnHome_Click(object sender, RoutedEventArgs e) => _selectedTab?.Navigate(GetHomePageUrl());

    private void BtnSettings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void BtnHistory_Click(object sender, RoutedEventArgs e) => OpenHistory();

    private void OpenHistory()
    {
        var existing = Tabs.FirstOrDefault(t => t.IsHistoryTab);
        if (existing is not null)
        {
            SelectTab(existing);
            return;
        }

        var tab = new BrowserTab(isSettingsTab: true, isHistoryTab: true);
        tab.CloseRequested += OnTabCloseRequested;
        var view = new Views.HistoryView();
        view.OpenRequested += (_, url) => _ = OpenNewTabAsync(url);
        tab.SetSettingsContent(view);
        Tabs.Add(tab);
        SelectTab(tab);
    }

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

    private void OnTabNavigated(object? sender, string url)
    {
        if (sender is not BrowserTab tab) return;
        if (url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return;
        HistoryService.Add(url, tab.Title);
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
        ApplyTraySettings();
        bool hotkeyOk = RegisterGlobalHotkey();
        if (sender is Views.SettingsView view) view.SetHotkeyStatus(hotkeyOk);
        UpdateWindowTitle();
        // User-Agent 对所有标签页的后续导航生效
        foreach (var tab in Tabs)
        {
            tab.ApplyUserAgent();
        }
    }

    // ==================== 防捕获（SetWindowDisplayAffinity） ====================

    private void ApplyCaptureMode()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        DisplayAffinity.Apply(hwnd, App.Settings.CaptureMode);
        // 任务栏/窗口图标随捕获模式切换
        Icon = Services.AppIconFactory.GetModeIcon(App.Settings.CaptureMode);
        UpdateTrayIconImage();
        // DevTools / 独立下载窗口等附属窗口不会自动继承主窗口的亲和性，由监视器覆盖
        if (App.Settings.CaptureMode == Models.CaptureMode.Capturable)
        {
            _affinityWatcher.Stop();
        }
        else
        {
            _affinityWatcher.Start(hwnd);
        }
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
        // 无边框窗口最大化时会向四周外扩 ResizeBorderThickness，预留边距避免内容被裁掉
        RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(6, 6, 6, 6) : new Thickness(0);
        BtnMaximize.Content = WindowState == WindowState.Maximized ? '\uE923' : '\uE922'; // ChromeRestore / ChromeMaximize
        ToolTipService.SetToolTip(BtnMaximize, WindowState == WindowState.Maximized ? "还原" : "最大化");

        if (WindowState == WindowState.Minimized)
        {
            if (App.Settings.MuteOnHide || App.Settings.PauseOnHide) SilenceMedia(true);
            if (App.Settings.MinimizeToTray) Hide(); // 最小化收进托盘
        }
        else if (!_isHiddenByHotkey)
        {
            SilenceMedia(false);
        }
        UpdateTrayVisibility();
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

    /// <summary>按标签数量与可用宽度计算每个标签的宽度和尺寸模式（增减标签时带宽度动画）。</summary>
    private void UpdateTabWidths(bool animate = false)
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
            tab.TabWidthMode = tab.Pinned ? TabSizingMode.Icon : mode;
            double target = tab.Pinned ? MinTabWidth : w;
            double old = tab.TabWidth;
            tab.TabWidth = target;

            if (animate && Math.Abs(old - target) > 0.5 && GetTabButton(tab) is { } btn)
            {
                var animation = new DoubleAnimation(old, target, TimeSpan.FromMilliseconds(180))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    FillBehavior = FillBehavior.Stop,
                };
                btn.BeginAnimation(FrameworkElement.WidthProperty, animation);
            }
        }
    }

    /// <summary>取标签对应的可视化按钮（ContentPresenter 内的 Button）。</summary>
    private Button? GetTabButton(BrowserTab tab)
    {
        if (TabsList.ItemContainerGenerator.ContainerFromItem(tab) is not ContentPresenter presenter) return null;
        return FindVisualChild<Button>(presenter);
    }

    private static T? FindVisualChild<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            var result = FindVisualChild<T>(child);
            if (result is not null) return result;
        }
        return null;
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

    private void TabScroll_LostMouseCapture(object sender, MouseEventArgs e)
    {
        // LostMouseCapture 是冒泡事件：拖动开始时标签按钮失去捕获会冒泡到这里，
        // 仅当 TabScroll 自身失去捕获（真正结束）时才终止拖动
        if (ReferenceEquals(e.OriginalSource, TabScroll)) EndTabDrag();
    }

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

    // ==================== 标签页右键菜单 ====================

    private void TabContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: BrowserTab tab } || tab.WebView is null) return;
        if (sender is not FrameworkElement { ContextMenu: ContextMenu menu }) return;

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            switch (item.Tag as string)
            {
                case "pin":
                    item.Header = tab.Pinned ? "取消固定标签页" : "固定标签页";
                    break;
                case "mute":
                    var core = tab.WebView.CoreWebView2;
                    item.Header = core is { IsMuted: true } ? "取消静音标签页" : "静音标签页";
                    break;
                case "others":
                    item.IsEnabled = Tabs.Count > 1;
                    break;
                case "right":
                    item.IsEnabled = Tabs.IndexOf(tab) < Tabs.Count - 1;
                    break;
            }
        }
    }

    private void MenuReload_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab { IsWebViewReady: true } tab) tab.Reload();
    }

    private void MenuCopyUrl_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            try { Clipboard.SetText(tab.CurrentUrl); } catch { /* 剪贴板被占用时忽略 */ }
        }
    }

    private void MenuMute_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            var core = tab.WebView?.CoreWebView2;
            if (core is not null) core.IsMuted = !core.IsMuted;
        }
    }

    private void MenuDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab && tab.CurrentUrl.Length > 0)
        {
            _ = OpenNewTabAsync(tab.CurrentUrl);
        }
    }

    private void MenuPin_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is BrowserTab tab)
        {
            tab.Pinned = !tab.Pinned;
            UpdateTabWidths(animate: true);
        }
    }

    private void MenuCloseOthers_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserTab keep) return;
        foreach (var tab in Tabs.ToList())
        {
            if (tab != keep) OnTabCloseRequested(tab, EventArgs.Empty);
        }
    }

    private void MenuCloseRight_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not BrowserTab tab) return;
        int index = Tabs.IndexOf(tab);
        for (int i = Tabs.Count - 1; i > index; i--)
        {
            OnTabCloseRequested(Tabs[i], EventArgs.Empty);
        }
    }

    private void MenuCloseDuplicates_Click(object sender, RoutedEventArgs e)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in Tabs.ToList())
        {
            if (!seen.Add(tab.CurrentUrl))
            {
                OnTabCloseRequested(tab, EventArgs.Empty);
            }
        }
    }

    // ==================== 标签页停放（Memory Saver） ====================

    private readonly DispatcherTimer _parkingTimer = new() { Interval = TimeSpan.FromMinutes(1) };

    private void StartParkingTimer()
    {
        _parkingTimer.Tick += (_, _) =>
        {
            if (!App.Settings.TabParkingEnabled) return;
            var deadline = DateTime.Now.AddMinutes(-App.Settings.TabParkingTimeoutMinutes);
            foreach (var tab in Tabs)
            {
                if (tab.Pinned) continue; // 固定标签不停放
                if (!ReferenceEquals(tab, _selectedTab) && tab.LastActiveTime < deadline)
                {
                    tab.Park();
                }
            }
        };
        _parkingTimer.Start();
    }

    // ==================== 窗口级快捷键（焦点在 WPF 控件上时） ====================
    // Ctrl+T/W/L/K/Ctrl+Tab 已由 AppKeyboardHook 在前台统一拦截（网页获得焦点时同样生效）

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Home)
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
