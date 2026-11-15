using Microsoft.Win32;
using System.Windows;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;

namespace AntiCaptureBrowser.Services;

public enum AppThemeMode
{
    Light,
    Dark,
}

/// <summary>
/// 跟随 Windows 系统深浅色主题：维护应用调色板（Application.Resources），
/// 监听系统主题变更并实时切换；同时为 WebView2 设置对应的 PreferredColorScheme。
/// </summary>
public static class ThemeManager
{
    /// <summary>主题切换（用于同步 WebView2 等非 XAML 资源的配色）。</summary>
    public static event Action? ThemeChanged;

    public static AppThemeMode Current { get; private set; } = AppThemeMode.Dark;

    // 资源键 → (深色值, 浅色值)
    private static readonly Dictionary<string, (Color Dark, Color Light)> Palette = new()
    {
        ["ChromeBgBrush"]      = (Col("#202124"), Col("#DFE1E5")),
        ["ToolbarBgBrush"]     = (Col("#292A2D"), Col("#FFFFFF")),
        ["TabActiveBrush"]     = (Col("#35363A"), Col("#FFFFFF")),
        ["ForegroundBrush"]    = (Col("#E8EAED"), Col("#202124")),
        ["ForegroundDimBrush"] = (Col("#9AA0A6"), Col("#5F6368")),
        ["AccentBrush"]        = (Col("#8AB4F8"), Col("#1A73E8")),
        ["HoverBrush"]         = (Col("#3C4043"), Col("#E8EAED")),
        ["Hover2Brush"]        = (Col("#2B2C2F"), Col("#F1F3F4")),
        ["FieldBgBrush"]       = (Col("#202124"), Col("#F1F3F4")),
        ["CardBgBrush"]        = (Col("#1E1F22"), Col("#FFFFFF")),
        ["CardBorderBrush"]    = (Col("#323336"), Col("#E8EAED")),
        ["BorderBrush"]        = (Col("#3C4043"), Col("#DADCE0")),
        ["BorderStrongBrush"]  = (Col("#5F6368"), Col("#BDC1C6")),
        ["AccentTintBrush"]    = (Col("#1E2B3D"), Col("#E8F0FE")),
        ["PageIconBgBrush"]    = (Col("#33415580"), Col("#D2E3FC")),
        ["PopupBgBrush"]       = (Col("#2B2C2F"), Col("#FFFFFF")),
        ["SelectedComboBrush"] = (Col("#3C5A99"), Col("#D2E3FC")),
        ["BadgeFgBrush"]       = (Col("#202124"), Col("#FFFFFF")),
        ["CloseHoverBrush"]    = (Col("#E81123"), Col("#E81123")),
        ["SuccessBrush"]       = (Col("#81C995"), Col("#1E8E3E")),
    };

    /// <summary>在应用启动时调用：按系统主题着色并开始监听系统变更。</summary>
    public static void Initialize()
    {
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.Color or UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)
            {
                Application.Current?.Dispatcher.Invoke(Apply);
            }
        };
    }

    public static bool IsSystemDark()
    {
        try
        {
            // 0 = 深色模式；键缺失时默认深色
            return Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 0) is 0;
        }
        catch
        {
            return true;
        }
    }

    public static void Apply()
    {
        Current = IsSystemDark() ? AppThemeMode.Dark : AppThemeMode.Light;
        var resources = Application.Current.Resources;
        foreach (var (key, (dark, light)) in Palette)
        {
            var brush = new SolidColorBrush(Current == AppThemeMode.Dark ? dark : light);
            brush.Freeze();
            resources[key] = brush;
        }
        ThemeChanged?.Invoke();
    }

    /// <summary>WebView2 页面应使用的配色方案（prefers-color-scheme）。</summary>
    public static CoreWebView2PreferredColorScheme WebViewColorScheme => Current == AppThemeMode.Dark
        ? CoreWebView2PreferredColorScheme.Dark
        : CoreWebView2PreferredColorScheme.Light;

    private static Color Col(string hex) => (Color)ColorConverter.ConvertFromString(hex);
}
