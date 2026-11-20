namespace AntiCaptureBrowser.Models;

public class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Time { get; set; }

    /// <summary>列表展示用的时间文本。</summary>
    public string TimeText => Time.ToString("yyyy-MM-dd HH:mm");
}
