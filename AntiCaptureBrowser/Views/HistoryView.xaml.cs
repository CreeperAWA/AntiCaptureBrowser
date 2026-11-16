using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AntiCaptureBrowser.Models;
using AntiCaptureBrowser.Services;

namespace AntiCaptureBrowser.Views;

public partial class HistoryView : UserControl
{
    /// <summary>请求在新标签页中打开指定网址。</summary>
    public event EventHandler<string>? OpenRequested;

    public HistoryView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void Refresh()
    {
        var items = HistoryService.Query(SearchBox.Text);
        HistoryList.ItemsSource = items
            .Select(e => new
            {
                e.Url,
                e.Title,
                TimeText = e.Time.ToString("yyyy-MM-dd HH:mm"),
            })
            .ToList();
        EmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        HistoryService.Clear();
        Refresh();
    }

    private void HistoryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntry entry && OpenRequested is not null)
        {
            OpenRequested(this, entry.Url);
        }
    }
}
