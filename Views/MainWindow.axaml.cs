using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

        Closing += OnClosing;
        Closed += (_, _) => _model?.Dispose();

        AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>
    /// On macOS closing the window does not quit the program: the window is hidden, the
    /// program stays in the Dock with its dot, and clicking the Dock icon brings it back
    /// (see <see cref="App"/>). Cmd+Q, Quit in the Dock menu, and logging out still quit,
    /// because those close the window with a shutdown reason rather than as a window close.
    /// Elsewhere, closing the main window quits, as Windows and Linux users expect.
    /// </summary>
    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (OperatingSystem.IsMacOS() && e.CloseReason == WindowCloseReason.WindowClosing &&
            !e.IsProgrammatic)
        {
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// Keyboard for the thumbnail selection: Space checks or unchecks the selected files,
    /// Cmd/Ctrl+A selects everything shown, Escape clears the selection. Only while the grid
    /// has focus, so typing in a text box or pressing Space on a button behaves as usual.
    /// </summary>
    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (_model is null || FocusManager?.GetFocusedElement() is not ThumbnailCard)
            return;

        var command = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);

        switch (e.Key)
        {
            case Key.Space when _model.ToggleSelectedCommand.CanExecute(null):
                _model.ToggleSelectedCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.A when command:
                _model.SelectAllCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                _model.ClearSelectionCommand.Execute(null);
                e.Handled = true;
                break;
        }
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
