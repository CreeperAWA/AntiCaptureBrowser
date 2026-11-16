using System.Net.Http;
using System.Text.Json;

namespace AntiCaptureBrowser.Services;

/// <summary>
/// 搜索引擎联想词：按当前搜索引擎调用其建议接口。
/// Google/Bing/DDG 使用 firefox 兼容的 JSON 列表格式，百度使用 sugrec 接口。
/// </summary>
public static class SuggestionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    public static async Task<string[]> GetSuggestionsAsync(string engineId, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return [];
        string url = engineId switch
        {
            "google" => $"https://www.google.com/complete/search?client=firefox&q={Uri.EscapeDataString(query)}",
            "bing" => $"https://api.bing.com/osjson.aspx?q={Uri.EscapeDataString(query)}",
            "duckduckgo" => $"https://duckduckgo.com/ac/?q={Uri.EscapeDataString(query)}&type=list",
            "baidu" => $"https://www.baidu.com/sugrec?prod=pc&from=pc_web&wd={Uri.EscapeDataString(query)}",
            _ => "",
        };
        if (url.Length == 0) return [];

        try
        {
            using var response = await Http.GetAsync(url);
            var json = await response.Content.ReadAsStringAsync();
            return engineId == "baidu" ? ParseBaidu(json) : ParseFirefoxFormat(json);
        }
        catch
        {
            return []; // 网络不可用/超时时静默降级
        }
    }

    /// <summary>["query", ["s1", "s2", ...]] 格式（Google/Bing/DDG）。</summary>
    private static string[] ParseFirefoxFormat(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array
                || doc.RootElement.GetArrayLength() < 2
                || doc.RootElement[1].ValueKind != JsonValueKind.Array) return [];
            return doc.RootElement[1].EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? "")
                .Where(s => s.Length > 0)
                .Take(8)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>{"g":[{"k":"s1"},...]} 格式（百度 sugrec）。</summary>
    private static string[] ParseBaidu(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("g", out var g)
                || g.ValueKind != JsonValueKind.Array) return [];
            return g.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("k", out _))
                .Select(x => x.GetProperty("k").GetString() ?? "")
                .Where(s => s.Length > 0)
                .Take(8)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
