# AntiCaptureBrowser 防屏幕捕获浏览器

基于 **C# / WPF / .NET 10 / WebView2** 的防屏幕捕获浏览器。核心原理：Win32 `SetWindowDisplayAffinity` 让窗口从所有屏幕捕获中排除或显示为黑色。

## 功能

- **防捕获三种模式**（设置页可切换，立即生效）：
  - `透明`（默认）—— `WDA_EXCLUDEFROMCAPTURE`：窗口在屏幕上正常显示，但在截图、录屏、屏幕共享、远程协助中**完全不可见**
  - `黑色` —— `WDA_MONITOR`：捕获结果中窗口显示为纯黑
  - `可捕获` —— `WDA_NONE`：正常捕获
- **标签页**：多标签浏览、新标签页打开 target=_blank 链接、Ctrl+T / Ctrl+W / Ctrl+Tab 切换
- **地址栏**：输入网址直接导航（自动补 `https://`），输入文字则用搜索引擎搜索
- **搜索引擎**：Google / Bing / DuckDuckGo / 百度，或自定义 URL 模板（含 `{query}` 或 `%s` 占位符）
- **主页**：自定义 URL 或空白页
- **全局热键**（默认 `Ctrl+Alt+B`，设置页可改）：任意程序中一键隐藏/恢复窗口
- **隐藏 / 最小化媒体行为**：自动静音所有标签页、可选暂停页面音视频；恢复时自动取消静音

## 数据存储（便携模式）

所有数据（settings.json、WebView2 用户数据/缓存/浏览记录）均存放在 **exe 所在目录的 `Data\` 文件夹**中，不写注册表和用户目录。

## 构建与运行

需要 .NET 10 SDK 和 WebView2 Runtime（Win11 / 新版 Win10 自带）。

```bash
dotnet build -c Release
dotnet run -c Release
# 或直接运行 bin\Release\net10.0-windows10.0.19041.0\AntiCaptureBrowser.exe
```

## 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+T` / `Ctrl+W` / `Ctrl+F4` | 新建 / 关闭标签页 |
| `Ctrl+Tab` / `Ctrl+Shift+Tab` | 切换标签页 |
| `Ctrl+L` / `Ctrl+K` | 聚焦地址栏 |
| `F5` / `Ctrl+R` | 刷新 |
| `Alt+←` / `Alt+→` | 后退 / 前进 |
| `Alt+Home` | 打开主页 |
| `Ctrl+Alt+B`（可配置） | 全局隐藏 / 恢复窗口 |

## 注意事项

- 组合模式渲染（`WebView2CompositionControl`）用于让键盘输入经过 WPF 管线，便于可靠拦截宿主级快捷键。
- `WDA_EXCLUDEFROMCAPTURE` 需要 Windows 10 2004+。
- 全局热键被其他程序占用时注册会失败，可在设置页换一个组合键。
