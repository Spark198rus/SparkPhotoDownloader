namespace SparkPhotoDownloader.ViewModels;

/// <summary>
/// The handful of things the view models need a window for: file pickers, dialogs, and
/// handing a path to the desktop's file manager.
/// </summary>
public interface IUiServices
{
    /// <summary>Show a folder picker. Returns null when the user cancels.</summary>
    Task<string?> PickFolderAsync(string title, string? startAt = null);

    /// <summary>Show a message with a single OK button.</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>Ask a yes/no question. Returns true for yes.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText = "OK");

    /// <summary>Ask for a single line of text. Returns null when the user cancels.</summary>
    Task<string?> PromptAsync(string title, string message, string? initialValue = null);

    /// <summary>Open a folder, or reveal a file, in the desktop's file manager.</summary>
    void RevealInFileManager(string path);

    /// <summary>Open the Preferences window.</summary>
    Task ShowPreferencesAsync();

    /// <summary>Open the download history window.</summary>
    Task ShowHistoryAsync();

    /// <summary>Open the naming preset editor for a preset list.</summary>
    Task<bool> ShowPresetEditorAsync(Models.FileKind kind, bool subfolders);
}
