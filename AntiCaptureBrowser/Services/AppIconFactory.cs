using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AntiCaptureBrowser.Models;

namespace AntiCaptureBrowser.Services;

/// <summary>
/// 应用图标工厂：按窗口捕获保护模式返回对应图标。
/// 优先加载 exe 目录 Assets 下的自定义图标（icon-protected / icon-dark / icon-open.png，
/// 用户后续绘制后放入即可覆盖），否则运行时绘制占位图标。
/// </summary>
public static class AppIconFactory
{
    public static BitmapSource GetModeIcon(CaptureMode mode, double size = 32)
    {
        string file = Path.Combine(AppContext.BaseDirectory, "Assets", mode switch
        {
            CaptureMode.Transparent => "icon-protected.png", // 透明：防捕获
            CaptureMode.Black => "icon-dark.png",            // 黑色：捕获时变黑
            _ => "icon-open.png",                            // 可捕获
        });
        if (File.Exists(file))
        {
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.UriSource = new Uri(file);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch
            {
                // 自定义图标损坏时回退到内置占位图
            }
        }
        return DrawPlaceholder(mode, size);
    }

    /// <summary>运行时绘制占位图标：圆角底色 + Segoe MDL2 字形。</summary>
    private static BitmapSource DrawPlaceholder(CaptureMode mode, double size)
    {
        (Color background, string glyph, double fontSize) = mode switch
        {
            CaptureMode.Transparent => (Color.FromRgb(0x1A, 0x73, 0xE8), "\uE72E", size * 0.5), // 蓝 + 锁（防捕获）
            CaptureMode.Black => (Color.FromRgb(0x20, 0x21, 0x24), "\uE7B3", size * 0.48),      // 黑 + 眼（捕获为黑）
            _ => (Color.FromRgb(0x34, 0xA8, 0x53), "\uE774", size * 0.48),                       // 绿 + 地球（可捕获）
        };

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(
                new SolidColorBrush(background), null,
                new Rect(1, 1, size - 2, size - 2), size * 0.18, size * 0.18);
            var text = new FormattedText(
                glyph,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe MDL2 Assets"),
                fontSize,
                Brushes.White,
                1.0);
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));
        }

        int pixels = (int)Math.Round(size);
        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
