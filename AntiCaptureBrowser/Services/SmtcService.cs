using Windows.Media;

namespace AntiCaptureBrowser.Services;

/// <summary>
/// 对接 Windows 系统媒体传输控制（SMTC）：音量弹窗与媒体键（播放/暂停）可控制
/// 当前出声的标签页。一个窗口一个 SMTC 实例，跟随最近开始出声的标签页（Chrome 同款策略）。
/// </summary>
public sealed class SmtcService
{
    private SystemMediaTransportControls? _smtc;

    /// <summary>系统请求播放（true）或暂停（false）。</summary>
    public event EventHandler<bool>? PlayPauseRequested;

    /// <summary>绑定窗口。ShowInTaskbar 切换会重建窗口句柄，需重新调用。</summary>
    public void Initialize(IntPtr hwnd)
    {
        try
        {
            var smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.ButtonPressed += (_, e) =>
            {
                if (e.Button == SystemMediaTransportControlsButton.Play) PlayPauseRequested?.Invoke(this, true);
                else if (e.Button == SystemMediaTransportControlsButton.Pause) PlayPauseRequested?.Invoke(this, false);
            };
            smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
            smtc.IsEnabled = false;
            _smtc = smtc;
        }
        catch
        {
            _smtc = null; // SMTC 不可用不影响主功能
        }
    }

    /// <summary>刷新 SMTC 状态与元数据（hasMedia=false 时隐藏系统媒体弹窗条目）。</summary>
    public void Update(string? title, string? artist, bool playing, bool hasMedia)
    {
        var smtc = _smtc;
        if (smtc is null) return;
        try
        {
            smtc.IsEnabled = hasMedia;
            if (!hasMedia)
            {
                smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                return;
            }
            var updater = smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = string.IsNullOrWhiteSpace(title) ? "正在播放" : title;
            updater.MusicProperties.Artist = artist ?? "";
            updater.Update();
            smtc.PlaybackStatus = playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch
        {
            // 元数据更新失败不影响播放
        }
    }
}
