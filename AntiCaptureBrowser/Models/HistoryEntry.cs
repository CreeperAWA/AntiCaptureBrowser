namespace AntiCaptureBrowser.Models;

public class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public DateTime Time { get; set; }
}
