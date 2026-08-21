using Avalonia;
using Avalonia.Media;
using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.ViewModels;

/// <summary>
/// The single place that decides how a <see cref="DownloadState"/> looks, so the thumbnail
/// grid, the filter menu and the summary bar can never drift apart.
/// </summary>
public static class StateVisuals
{
    public static IBrush Brush(DownloadState state) => Lookup(state switch
    {
        DownloadState.New => "StateNewBrush",
        DownloadState.PreviouslyDownloaded => "StatePreviousBrush",
        DownloadState.MissingAtDestination => "StateMissingBrush",
        DownloadState.Downloading => "StateNewBrush",
        DownloadState.Downloaded => "StateDoneBrush",
        DownloadState.Failed => "StateFailedBrush",
        _ => "StateNewBrush",
    });

    /// <summary>Short badge text drawn in the corner of a thumbnail. Empty means no badge.</summary>
    public static string Badge(DownloadState state) => state switch
    {
        DownloadState.PreviouslyDownloaded => "DOWNLOADED",
        DownloadState.MissingAtDestination => "MISSING",
        DownloadState.Downloading => "COPYING",
        DownloadState.Downloaded => "DONE",
        DownloadState.Failed => "FAILED",
        _ => string.Empty,
    };

    /// <summary>
    /// How faded the image is. Files already downloaded recede; a file whose copy has gone
    /// missing does not, because it is the one the user needs to notice.
    /// </summary>
    public static double Opacity(DownloadState state) => state switch
    {
        DownloadState.PreviouslyDownloaded => 0.42,
        DownloadState.Failed => 0.6,
        _ => 1.0,
    };

    /// <summary>The card border thickness: the flagged state gets a heavier outline.</summary>
    public static Thickness BorderThickness(DownloadState state) => state switch
    {
        DownloadState.MissingAtDestination => new Thickness(2),
        DownloadState.New => new Thickness(1),
        _ => new Thickness(1),
    };

    public static string Describe(DownloadState state) => state switch
    {
        DownloadState.New => "New",
        DownloadState.PreviouslyDownloaded => "Previously downloaded",
        DownloadState.MissingAtDestination => "Previously downloaded, missing at destination",
        DownloadState.Downloading => "Downloading",
        DownloadState.Downloaded => "Downloaded",
        DownloadState.Failed => "Failed",
        _ => state.ToString(),
    };

    public static IBrush KindBrush(FileKind kind) =>
        Lookup(kind == FileKind.Video ? "VideoAccentBrush" : "PhotoAccentBrush");

    /// <summary>A one-character mark used where there is no room for words.</summary>
    public static string KindGlyph(FileKind kind) => kind switch
    {
        FileKind.Video => "VIDEO",
        FileKind.Photo => "PHOTO",
        FileKind.Audio => "AUDIO",
        _ => "FILE",
    };

    private static IBrush Lookup(string key)
    {
        if (Application.Current?.TryGetResource(key, null, out var value) == true &&
            value is IBrush brush)
        {
            return brush;
        }

        return Brushes.Gray;
    }
}
