using System.Windows;
using AntiCaptureBrowser.Models;
using AntiCaptureBrowser.Services;

namespace AntiCaptureBrowser;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Settings = SettingsService.Load();
        // 按系统主题着色（深/浅），此后监听系统主题变更实时切换
        ThemeManager.Initialize();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SettingsService.Save(Settings);
        base.OnExit(e);
    }
}
