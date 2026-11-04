using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SparkPhotoDownloader.Views;

namespace SparkPhotoDownloader;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;

            // On macOS the main window hides rather than closes (see MainWindow.OnClosing),
            // so the program keeps running in the Dock. A click on the Dock icon arrives as a
            // Reopen activation and shows the window again.
            if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
            {
                activatable.Activated += (_, e) =>
                {
                    if (e.Kind != ActivationKind.Reopen)
                        return;

                    window.Show();
                    window.Activate();
                };
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
