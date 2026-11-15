using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AntiCaptureBrowser.Models;

namespace AntiCaptureBrowser.Services;

/// <summary>JSON 格式的设置持久化服务。</summary>
public static class SettingsService
{
    /// <summary>所有数据（配置、缓存、浏览数据）统一存放在 exe 所在目录的 Data 文件夹。</summary>
    private static string DataRoot => Path.Combine(AppContext.BaseDirectory, "Data");

    private static string FilePath => Path.Combine(DataRoot, "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
            }
        }
        catch
        {
            // 设置损坏时回退到默认值
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DataRoot);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
        }
        catch
        {
            // 写入失败时忽略（如磁盘只读），不影响运行
        }
    }
}
