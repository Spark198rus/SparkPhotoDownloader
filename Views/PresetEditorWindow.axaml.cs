using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SparkPhotoDownloader.Views;

public partial class PresetEditorWindow : Window
{
    public PresetEditorWindow() => InitializeComponent();

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
