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
}
