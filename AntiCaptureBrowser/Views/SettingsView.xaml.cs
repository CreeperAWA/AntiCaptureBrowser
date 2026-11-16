using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AntiCaptureBrowser.Models;
using AntiCaptureBrowser.Services;
using Microsoft.Web.WebView2.Core;

namespace AntiCaptureBrowser.Views;

public partial class SettingsView : UserControl
{
    /// <summary>设置发生变更并被应用（主窗口据此重新应用捕获模式/热键）。</summary>
    public event EventHandler? SettingsApplied;

    private bool _loading;
    private string _selectedCategory = "general";

    // 分类元数据：键、标题、搜索关键词、页面对象
    private readonly List<(string Key, string Title, string Keywords, FrameworkElement Page)> _sections = new();

    public SettingsView()
    {
        InitializeComponent();
        _flashTimer.Tick += (_, _) => SavedText.Visibility = Visibility.Hidden;
        _sections.Add(("general", "常规", "主页 新标签页 homepage 首页 起始页", PageGeneral));
        _sections.Add(("search", "搜索引擎", "搜索 google bing duckduckgo 百度 自定义 search engine", PageSearch));
        _sections.Add(("privacy", "隐私与捕获保护", "隐私 捕获 截图 录屏 屏幕共享 防捕获 透明 黑色 displayaffinity capture", PagePrivacy));
        _sections.Add(("hotkey", "快捷键", "快捷键 热键 隐藏 恢复 全局 hotkey key", PageHotkey));
        _sections.Add(("media", "隐藏与最小化", "隐藏 最小化 静音 暂停 媒体 音视频 声音 mute pause", PageMedia));
        _sections.Add(("ua", "User-Agent", "UA 浏览器标识 user agent 移动版 手机", PageUserAgent));
        _sections.Add(("about", "关于", "关于 版本 构建 提交 数据 存储 开源 组件 license data about", PageAbout));
        NavGeneral.IsChecked = true; // 默认进入「常规」分类
        Loaded += (_, _) =>
        {
            LoadFromSettings();
            FillAboutInfo();
        };
    }

    // ==================== 关于页数据 ====================

    private void FillAboutInfo()
    {
        var asm = Assembly.GetExecutingAssembly();
        var metadata = asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key ?? "", a => a.Value ?? "");

        string version = asm.GetName().Version?.ToString(3) ?? "1.0.0";
        AboutVersionText.Text = $"AntiCaptureBrowser v{version}（{(Environment.Is64BitProcess ? "64 位" : "32 位")}）";
        AboutChromiumText.Text = TryGet(() => $"WebView2 运行时（Chromium）：{CoreWebView2Environment.GetAvailableBrowserVersionString()}",
            "未检测到 WebView2 运行时");
        AboutRuntimeText.Text = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        AboutWebView2RuntimeText.Text = TryGet(() => CoreWebView2Environment.GetAvailableBrowserVersionString(), "未检测到");
        AboutWebView2SdkText.Text = TryGet(() => typeof(CoreWebView2Environment).Assembly.GetName().Version?.ToString() ?? "未知", "未知");
        AboutBuildTimeText.Text = metadata.GetValueOrDefault("BuildTimeUtc", "未知");
        string commit = metadata.GetValueOrDefault("CommitId", "unknown");
        AboutCommitText.Text = commit == "unknown" ? "unknown（非 Git 构建）" : commit;

        CompRuntimeText.Text = TryGet(() => $"版本 {Environment.Version}", "");
        CompWebView2Text.Text = TryGet(() => $"版本 {typeof(CoreWebView2Environment).Assembly.GetName().Version}", "");
        CompSdkNetText.Text = TryGet(() =>
        {
            var sdk = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Windows.SDK.NET")
                ?? Assembly.Load("Microsoft.Windows.SDK.NET");
            return $"版本 {sdk.GetName().Version}";
        }, "随运行时加载");
        AboutCopyrightText.Text = $"版权所有 © {DateTime.Now.Year} AntiCaptureBrowser。基于 MIT 许可证授权，可自由使用、修改与分发。";
    }

    private static string TryGet(Func<string> factory, string fallback)
    {
        try
        {
            string value = factory();
            return string.IsNullOrEmpty(value) ? fallback : value;
        }
        catch
        {
            return fallback;
        }
    }

    // ==================== 分类导航与搜索 ====================

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_sections.Count == 0) return;
        if (sender is RadioButton rb)
        {
            _selectedCategory = rb.Name switch
            {
                nameof(NavGeneral) => "general",
                nameof(NavSearch) => "search",
                nameof(NavPrivacy) => "privacy",
                nameof(NavHotkey) => "hotkey",
                nameof(NavMedia) => "media",
                nameof(NavUserAgent) => "ua",
                nameof(NavAbout) => "about",
                _ => _selectedCategory,            };
            ApplyFilter();
            ContentScroll?.ScrollToTop();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ApplyFilter()
    {
        if (_sections.Count == 0) return;
        string query = SearchBox.Text.Trim();
        bool anyVisible = false;

        foreach (var section in _sections)
        {
            bool show = query.Length == 0
                ? section.Key == _selectedCategory
                : (section.Title + " " + section.Keywords).Contains(query, StringComparison.OrdinalIgnoreCase);
            section.Page.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            anyVisible |= show;
        }

        NoResultsText.Visibility = anyVisible ? Visibility.Collapsed : Visibility.Visible;
        SearchPlaceholder.Visibility = query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ==================== 加载与保存 ====================

    private void LoadFromSettings()
    {
        var s = App.Settings;
        _loading = true;
        try
        {
            HomePageModeCombo.SelectedIndex = s.HomePageMode == HomePageMode.CustomUrl ? 0 : 1;
            HomePageUrlBox.Text = s.HomePageUrl;
            SearchEngineCombo.SelectedIndex = s.SearchEngineId switch
            {
                "google" => 0,
                "bing" => 1,
                "duckduckgo" => 2,
                "baidu" => 3,
                "custom" => 4,
                _ => 1,
            };
            CustomSearchUrlBox.Text = s.CustomSearchUrl;
            CaptureCapturable.IsChecked = s.CaptureMode == CaptureMode.Capturable;
            CaptureBlack.IsChecked = s.CaptureMode == CaptureMode.Black;
            CaptureTransparent.IsChecked = s.CaptureMode == CaptureMode.Transparent;
            HideHotkeyBox.HotkeyValue = s.HideHotkey;
            MuteOnHideCheck.IsChecked = s.MuteOnHide;
            PauseOnHideCheck.IsChecked = s.PauseOnHide;
            UserAgentCombo.SelectedIndex = s.UserAgentOption switch
            {
                "default" => 0,
                "win-chrome" => 1,
                "win-edge" => 2,
                "android" => 3,
                "iphone" => 4,
                "mac-safari" => 5,
                "custom" => 6,
                _ => 0,
            };
            CustomUserAgentBox.Text = s.CustomUserAgent;
        }
        finally
        {
            _loading = false;
        }
        UpdateCustomVisibility();
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        var s = App.Settings;
        s.HomePageMode = HomePageModeCombo.SelectedIndex == 1 ? HomePageMode.Blank : HomePageMode.CustomUrl;
        s.HomePageUrl = HomePageUrlBox.Text.Trim();
        s.SearchEngineId = ((ComboBoxItem)SearchEngineCombo.SelectedItem)?.Tag as string ?? "bing";
        s.CustomSearchUrl = CustomSearchUrlBox.Text.Trim();
        s.CaptureMode =
            CaptureTransparent.IsChecked == true ? CaptureMode.Transparent :
            CaptureBlack.IsChecked == true ? CaptureMode.Black : CaptureMode.Capturable;
        s.HideHotkey = HideHotkeyBox.HotkeyValue;
        s.MuteOnHide = MuteOnHideCheck.IsChecked == true;
        s.PauseOnHide = PauseOnHideCheck.IsChecked == true;
        s.UserAgentOption = ((ComboBoxItem)UserAgentCombo.SelectedItem)?.Tag as string ?? "default";
        s.CustomUserAgent = CustomUserAgentBox.Text.Trim();

        SettingsService.Save(s);
        FlashSaved();
        UpdateCustomVisibility();
        SettingsApplied?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateCustomVisibility()
    {
        bool isCustom = SearchEngineCombo.SelectedIndex == 4;
        bool homeIsUrl = HomePageModeCombo.SelectedIndex == 0;
        CustomSearchUrlBox.Visibility = isCustom ? Visibility.Visible : Visibility.Collapsed;
        HomePageUrlBox.Visibility = homeIsUrl ? Visibility.Visible : Visibility.Collapsed;
        CustomUserAgentBox.Visibility = UserAgentCombo.SelectedIndex == 6 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>主窗口注册热键后回调状态。</summary>
    public void SetHotkeyStatus(bool ok)
    {
        HotkeyStatusText.Text = ok
            ? "✓ 热键已注册，随时按下即可隐藏/恢复窗口"
            : "⚠ 热键注册失败（可能被其他程序占用），请更换组合键后重试";
        HotkeyStatusText.Foreground = ok
            ? FindResource("SuccessBrush") as System.Windows.Media.Brush
            : System.Windows.Media.Brushes.OrangeRed;
    }

    private void FlashSaved()
    {
        SavedText.Visibility = Visibility.Visible;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private readonly DispatcherTimer _flashTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2),
    };
}
