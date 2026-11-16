using System.IO;
using System.Text.Json;
using AntiCaptureBrowser.Models;

namespace AntiCaptureBrowser.Services;

/// <summary>浏览历史：存于 exe 目录 Data\history.json，最新在前，最多保留 1000 条。</summary>
public static class HistoryService
{
    private const int MaxEntries = 1000;

    private static string FilePath => Path.Combine(AppContext.BaseDirectory, "Data", "history.json");

    private static List<HistoryEntry>? _entries;

    private static List<HistoryEntry> Entries
    {
        get
        {
            if (_entries is not null) return _entries;
            try
            {
                _entries = File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? []
                    : [];
            }
            catch
            {
                _entries = [];
            }
            return _entries;
        }
    }

    public static void Add(string url, string title)
    {
        var list = Entries;
        list.RemoveAll(e => e.Url == url); // 同址去重，最新在前
        list.Insert(0, new HistoryEntry { Url = url, Title = title, Time = DateTime.Now });
        if (list.Count > MaxEntries)
        {
            list.RemoveRange(MaxEntries, list.Count - MaxEntries);
        }
        Save();
    }

    public static IReadOnlyList<HistoryEntry> Query(string? keyword)
    {
        var list = Entries;
        if (string.IsNullOrWhiteSpace(keyword)) return list.Take(300).ToList();
        keyword = keyword.Trim();
        return list
            .Where(e => e.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                     || e.Url.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .Take(300)
            .ToList();
    }

    public static void Clear()
    {
        Entries.Clear();
        Save();
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Entries));
        }
        catch
        {
            // 磁盘写入失败时忽略，不影响浏览
        }
    }
}
