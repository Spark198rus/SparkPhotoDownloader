using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SparkPhotoDownloader.Views;

public partial class PreferencesWindow : Window
{
    public PreferencesWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
