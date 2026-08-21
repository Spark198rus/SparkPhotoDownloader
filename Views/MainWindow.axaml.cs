using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using SparkPhotoDownloader.Models;
using SparkPhotoDownloader.ViewModels;

namespace SparkPhotoDownloader.Views;

/// <summary>
/// The main window. It also supplies <see cref="IUiServices"/>, since pickers and dialogs
/// need a parent window and the view models must not know about one.
/// </summary>
public partial class MainWindow : Window, IUiServices
{
    private MainViewModel? _model;

    public MainWindow()
    {
        InitializeComponent();

        _model = new MainViewModel(this);
        DataContext = _model;

        _model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.ThumbnailSize))
                ApplyThumbnailSize();
        };

        ApplyThumbnailSize();

        Opened += async (_, _) =>
        {
            if (_model is not null)
                await _model.InitializeAsync();
        };

        Closing += (_, _) => _model?.Dispose();
    }

    /// <summary>
    /// Push the thumbnail size slider into the grid layout. The caption under each preview
    /// needs a fixed allowance on top of the image itself.
    /// </summary>
    private void ApplyThumbnailSize()
    {
        if (_model is null || ThumbnailGrid.Layout is not UniformGridLayout layout)
            return;

        layout.MinItemWidth = _model.ThumbnailSize;
        layout.MinItemHeight = _model.ThumbnailSize + 38;
    }

    // ---- IUiServices ---------------------------------------------------------------------

    public async Task<string?> PickFolderAsync(string title, string? startAt = null)
    {
        IStorageFolder? start = null;

        if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt))
        {
            try
            {
                start = await StorageProvider.TryGetFolderFromPathAsync(startAt);
            }
            catch (Exception)
            {
                // A stale saved path simply means the picker opens wherever it likes.
            }
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = start,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public Task ShowMessageAsync(string title, string message) =>
        MessageDialog.ShowAsync(this, title, message, okOnly: true);

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK") =>
        await MessageDialog.ShowAsync(this, title, message, okOnly: false, confirmText) is true;

    public Task<string?> PromptAsync(string title, string message, string? initialValue = null) =>
        TextPromptDialog.ShowAsync(this, title, message, initialValue);

    public void RevealInFileManager(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Select the file when it exists, otherwise open the closest folder that does.
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                    {
                        UseShellExecute = true,
                    });
                    return;
                }

                var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                while (folder is not null && !Directory.Exists(folder))
                    folder = Path.GetDirectoryName(folder);

                if (folder is not null)
                    Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });

                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                // "open -R" reveals and selects the file in Finder; opening it directly would
                // launch it in Preview instead.
                var info = new ProcessStartInfo("open") { UseShellExecute = false };
                if (File.Exists(path))
                    info.ArgumentList.Add("-R");
                info.ArgumentList.Add(path);
                Process.Start(info);
                return;
            }

            var target = File.Exists(path) ? Path.GetDirectoryName(path) ?? path : path;
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No file manager available; there is nothing useful to fall back to.
        }
    }

    public async Task ShowPreferencesAsync()
    {
        if (_model is null)
            return;

        var window = new PreferencesWindow
        {
            DataContext = new PreferencesViewModel(_model),
        };

        await window.ShowDialog(this);
    }

    public async Task ShowHistoryAsync()
    {
        if (_model is null)
            return;

        var window = new HistoryWindow
        {
            DataContext = new HistoryViewModel(_model.History, this),
        };

        await window.ShowDialog(this);
    }

    public async Task<bool> ShowPresetEditorAsync(FileKind kind, bool subfolders)
    {
        if (_model is null)
            return false;

        var editor = new PresetEditorViewModel(_model.SettingsService, kind, subfolders, this);
        var window = new PresetEditorWindow { DataContext = editor };

        await window.ShowDialog(this);
        return editor.Saved;
    }
}
