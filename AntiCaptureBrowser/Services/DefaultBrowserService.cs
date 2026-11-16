using System.Diagnostics;
using Microsoft.Win32;

namespace AntiCaptureBrowser.Services;

/// <summary>
/// 设为默认浏览器：按 Windows 官方文档的标准方式注册应用
/// （HKCU\Software\Clients\StartMenuInternet + Capabilities + RegisteredApplications），
/// 不直接修改 UserChoice（该键受系统哈希保护，伪造属于不当行为）。
/// 注册后打开系统「默认应用」设置页，由用户确认选择。
/// </summary>
public static class DefaultBrowserService
{
    private const string KeyName = "AntiCaptureBrowser";

    private static string ExePath => Environment.ProcessPath
        ?? Path.Combine(AppContext.BaseDirectory, "AntiCaptureBrowser.exe");

    /// <summary>以当前用户身份完成标准浏览器注册（幂等）。</summary>
    public static void RegisterAsBrowser()
    {
        string exe = ExePath;

        // 1. Clients\StartMenuInternet 注册（应用列表与默认应用页面依赖此项）
        using (var client = Registry.CurrentUser.CreateSubKey(@"Software\Clients\StartMenuInternet\" + KeyName))
        {
            client.SetValue(null, KeyName);
            client.SetValue("LocalizedString", "AntiCaptureBrowser");

            using var capabilities = client.CreateSubKey("Capabilities");
            capabilities.SetValue("ApplicationName", "AntiCaptureBrowser");
            capabilities.SetValue("ApplicationIcon", $"\"{exe}\",0");
            capabilities.SetValue("StartMenuInternet", KeyName);

            using var urlAssociations = capabilities.CreateSubKey("URLAssociations");
            urlAssociations.SetValue("http", KeyName + "URL");
            urlAssociations.SetValue("https", KeyName + "URL");

            using var shellOpen = client.CreateSubKey(@"shell\open\command");
            shellOpen.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        // 2. URL 协议处理类（http/https 关联指向它）
        using (var urlClass = Registry.CurrentUser.CreateSubKey(@"Software\Classes\" + KeyName + "URL"))
        {
            urlClass.SetValue("URL Protocol", "");
            urlClass.SetValue("FriendlyTypeName", "AntiCaptureBrowser");
            using var cmd = urlClass.CreateSubKey(@"shell\open\command");
            cmd.SetValue(null, $"\"{exe}\" \"%1\"");
        }

        // 3. RegisteredApplications（默认应用设置页枚举依据）
        using (var registered = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
        {
            registered.SetValue(KeyName,
                @"Software\Clients\StartMenuInternet\" + KeyName + @"\Capabilities");
        }
    }

    /// <summary>打开系统「默认应用」设置页让用户确认选择。</summary>
    public static void OpenDefaultAppsSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true });
    }
}
