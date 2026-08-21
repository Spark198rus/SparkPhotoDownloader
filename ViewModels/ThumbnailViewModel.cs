using System.Text;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.ViewModels;

/// <summary>One card in the thumbnail grid: a file, its preview, and its download state.</summary>
public sealed partial class ThumbnailViewModel : ViewModelBase
{
    private readonly Action<ThumbnailViewModel>? _onCheckedChanged;

    public ThumbnailViewModel(
        SourceFile file,
        HistoryLookup history,
        DateTime effectiveDate,
        Action<ThumbnailViewModel>? onCheckedChanged = null)
    {
        File = file;
        HistoryRecord = history.Record;
        DestinationStatus = history.Destination;
        EffectiveDate = effectiveDate;
        _onCheckedChanged = onCheckedChanged;

        State = history.ToState();
    }

    /// <summary>The owning view model, so a card can reach the commands in its context menu.</summary>
    public MainViewModel? Owner { get; set; }

    public SourceFile File { get; }

    /// <summary>The history row this file matched, if any.</summary>
    public DownloadRecord? HistoryRecord { get; private set; }

    public DestinationStatus DestinationStatus { get; private set; }

    /// <summary>Capture date when known, modification time otherwise. Drives sorting and the Timeline.</summary>
    public DateTime EffectiveDate { get; }

    public string FileName => File.FileName;

    public string Extension => File.Extension.ToUpperInvariant();

    public FileKind Kind => File.Kind;

    public long Size => File.Size;

    public string SizeText => FormatBytes(File.Size);

    public string DateText => EffectiveDate.ToString("d MMM yyyy HH:mm");

    public string SourceName => File.SourceName;

    // ---- Mutable state -------------------------------------------------------------------

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    [ObservableProperty]
    public partial bool ThumbnailFailed { get; set; }

    [NotifyPropertyChangedFor(nameof(StateBrush))]
    [NotifyPropertyChangedFor(nameof(Badge))]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    [NotifyPropertyChangedFor(nameof(ImageOpacity))]
    [NotifyPropertyChangedFor(nameof(BorderThickness))]
    [NotifyPropertyChangedFor(nameof(StateDescription))]
    [NotifyPropertyChangedFor(nameof(IsMissingAtDestination))]
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [ObservableProperty]
    public partial DownloadState State { get; set; }

    /// <summary>Set when a download of this file failed, for the tooltip.</summary>
    [NotifyPropertyChangedFor(nameof(Tooltip))]
    [ObservableProperty]
    public partial string? LastError { get; set; }

    // ---- Derived visuals -----------------------------------------------------------------

    public IBrush StateBrush => StateVisuals.Brush(State);

    public IBrush KindBrush => StateVisuals.KindBrush(Kind);

    public string KindGlyph => StateVisuals.KindGlyph(Kind);

    public string Badge => StateVisuals.Badge(State);

    public bool HasBadge => Badge.Length > 0;

    public double ImageOpacity => StateVisuals.Opacity(State);

    public Avalonia.Thickness BorderThickness => StateVisuals.BorderThickness(State);

    public string StateDescription => StateVisuals.Describe(State);

    /// <summary>
    /// The case this program exists to flag: downloaded once, and the copy is no longer where
    /// it was put.
    /// </summary>
    public bool IsMissingAtDestination => State == DownloadState.MissingAtDestination;

    public bool WasDownloadedBefore =>
        State is DownloadState.PreviouslyDownloaded or DownloadState.MissingAtDestination;

    public string Tooltip => BuildTooltip();

    partial void OnIsCheckedChanged(bool value) => _onCheckedChanged?.Invoke(this);

    /// <summary>
    /// Re-evaluate against the history, e.g. after the user forgets a file or a destination
    /// drive is reconnected.
    /// </summary>
    public void ApplyHistory(HistoryLookup lookup)
    {
        HistoryRecord = lookup.Record;
        DestinationStatus = lookup.Destination;
        State = lookup.ToState();
        OnPropertyChanged(nameof(WasDownloadedBefore));
        OnPropertyChanged(nameof(Tooltip));
    }

    /// <summary>Record the outcome of a download that just ran.</summary>
    public void ApplyDownloadResult(DownloadFileResultLike result)
    {
        State = result.Success ? DownloadState.Downloaded : DownloadState.Failed;
        LastError = result.Error;

        if (result.Success && result.DestinationPath is not null)
        {
            HistoryRecord = new DownloadRecord
            {
                FileName = File.FileName,
                MTime = File.Identity.MTime,
                Size = File.Size,
                DownloadPath = result.DestinationPath,
                DownloadedAtUtc = DateTime.UtcNow,
                SourceName = File.SourceName,
                SourcePath = File.FullPath,
                Kind = File.Kind,
            };
            DestinationStatus = Models.DestinationStatus.Present;
            IsChecked = false;
        }

        OnPropertyChanged(nameof(WasDownloadedBefore));
        OnPropertyChanged(nameof(Tooltip));
    }

    private string BuildTooltip()
    {
        var text = new StringBuilder();
        text.Append(FileName).Append('\n');
        text.Append(SizeText).Append("  ·  ").Append(DateText).Append('\n');
        text.Append(File.FullPath);

        if (File.Sidecars.Count > 0)
            text.Append("\n+ ").Append(File.Sidecars.Count).Append(" sidecar file(s)");

        if (HistoryRecord is { } record)
        {
            text.Append("\n\n");

            switch (DestinationStatus)
            {
                case Models.DestinationStatus.Missing:
                    text.Append("Downloaded on ")
                        .Append(record.DownloadedAtLocal.ToString("d MMM yyyy 'at' HH:mm"))
                        .Append(" to:\n")
                        .Append(record.DownloadPath)
                        .Append("\n\nThat copy is no longer there. It will not be downloaded ")
                        .Append("again unless you forget it first (right-click → Forget).");
                    break;

                case Models.DestinationStatus.Offline:
                    text.Append("Downloaded on ")
                        .Append(record.DownloadedAtLocal.ToString("d MMM yyyy 'at' HH:mm"))
                        .Append(" to:\n")
                        .Append(record.DownloadPath)
                        .Append("\n\nThat drive is not connected, so the copy could not be checked.");
                    break;

                default:
                    text.Append("Downloaded on ")
                        .Append(record.DownloadedAtLocal.ToString("d MMM yyyy 'at' HH:mm"))
                        .Append(" to:\n")
                        .Append(record.DownloadPath);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(record.JobCode))
                text.Append("\nJob Code: ").Append(record.JobCode);
        }

        if (!string.IsNullOrWhiteSpace(LastError))
            text.Append("\n\nError: ").Append(LastError);

        return text.ToString();
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }
}

/// <summary>
/// The parts of a download result the thumbnail cares about, kept separate so the view model
/// does not depend on the engine's types.
/// </summary>
/// <param name="Success">Whether the file made it to the destination.</param>
/// <param name="DestinationPath">Where it landed.</param>
/// <param name="Error">Why it did not, when it did not.</param>
public readonly record struct DownloadFileResultLike(
    bool Success, string? DestinationPath, string? Error);
