using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SparkPhotoDownloader.Views;

public partial class HistoryWindow : Window
{
    public HistoryWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
