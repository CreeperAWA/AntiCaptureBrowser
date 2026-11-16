namespace AntiCaptureBrowser.Models;

/// <summary>窗口捕获保护模式，对应 SetWindowDisplayAffinity 的参数。</summary>
public enum CaptureMode
{
    /// <summary>WDA_NONE：窗口可被正常捕获。</summary>
    Capturable = 0,

    /// <summary>WDA_MONITOR：窗口在截图/录屏中显示为黑色。</summary>
    Black = 1,

    /// <summary>WDA_EXCLUDEFROMCAPTURE：窗口在截图/录屏/屏幕共享中不可见（透明）。</summary>
    Transparent = 2,
}

/// <summary>主页（新标签页）模式。</summary>
public enum HomePageMode
{
    /// <summary>打开自定义 URL。</summary>
    CustomUrl = 0,

    /// <summary>打开空白页。</summary>
    Blank = 1,
}

public class SearchEngine
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    /// <summary>搜索 URL 模板，{query} 或 %s 会被替换为转义后的搜索词。</summary>
    public required string UrlTemplate { get; init; }
}

public static class SearchEngines
{
    public static readonly IReadOnlyList<SearchEngine> BuiltIn =
    [
        new SearchEngine { Id = "google", Name = "Google", UrlTemplate = "https://www.google.com/search?q={query}" },
        new SearchEngine { Id = "bing", Name = "Bing", UrlTemplate = "https://www.bing.com/search?q={query}" },
        new SearchEngine { Id = "duckduckgo", Name = "DuckDuckGo", UrlTemplate = "https://duckduckgo.com/?q={query}" },
        new SearchEngine { Id = "baidu", Name = "百度", UrlTemplate = "https://www.baidu.com/s?wd={query}" },
    ];

    public static string ResolveTemplate(string id, string customUrl)
    {
        if (string.Equals(id, "custom", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(customUrl) ? BuiltIn[1].UrlTemplate : customUrl;
        }
        return BuiltIn.FirstOrDefault(e => e.Id == id)?.UrlTemplate ?? BuiltIn[1].UrlTemplate;
    }
}

/// <summary>应用设置（持久化到 %APPDATA%\AntiCaptureBrowser\settings.json）。</summary>
public class AppSettings
{
    public HomePageMode HomePageMode { get; set; } = HomePageMode.CustomUrl;
    public string HomePageUrl { get; set; } = "https://www.bing.com";

    /// <summary>内置搜索引擎 Id，或 "custom"。</summary>
    public string SearchEngineId { get; set; } = "bing";

    /// <summary>自定义搜索引擎 URL 模板（含 {query} 或 %s 占位符）。</summary>
    public string CustomSearchUrl { get; set; } = "https://duckduckgo.com/?q={query}";

    public CaptureMode CaptureMode { get; set; } = CaptureMode.Transparent;

    /// <summary>隐藏/恢复窗口的全局热键，如 "Ctrl+Alt+B"。</summary>
    public string HideHotkey { get; set; } = "Ctrl+Alt+B";

    /// <summary>隐藏/最小化时静音所有标签页。</summary>
    public bool MuteOnHide { get; set; } = true;

    /// <summary>隐藏/最小化时暂停页面中的音视频。</summary>
    public bool PauseOnHide { get; set; } = false;

    // ===== 窗口状态记忆 =====
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public string WindowState { get; set; } = "Normal";

    // ===== User-Agent =====
    /// <summary>UA 预设：default / win-chrome / win-edge / android / iphone / mac-safari / custom。</summary>
    public string UserAgentOption { get; set; } = "default";

    public string CustomUserAgent { get; set; } = "";

    /// <summary>解析为实际 UA；null 表示使用 WebView2 默认值。</summary>
    public string? ResolveUserAgent() => UserAgentOption switch
    {
        "default" => null,
        "win-chrome" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36",
        "win-edge" => "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36 Edg/141.0.0.0",
        "android" => "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Mobile Safari/537.36",
        "iphone" => "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
        "mac-safari" => "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15",
        "custom" => string.IsNullOrWhiteSpace(CustomUserAgent) ? null : CustomUserAgent.Trim(),
        _ => null,
    };
}
