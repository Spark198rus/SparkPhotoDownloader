namespace SparkPhotoDownloader.Models;

/// <summary>
/// One row of the persistent download history: proof that a given source file was already
/// copied, and where it was copied to.
/// </summary>
public sealed class DownloadRecord
{
    public required string FileName { get; init; }
    public required double MTime { get; init; }
    public required long Size { get; init; }

    /// <summary>Absolute path the file was written to, including the generated name.</summary>
    public required string DownloadPath { get; init; }

    public required DateTime DownloadedAtUtc { get; init; }

    public string? SourceName { get; init; }
    public string? SourcePath { get; init; }
    public string? JobCode { get; init; }
    public FileKind Kind { get; init; }

    /// <summary>Backup copies written for this file, newline separated.</summary>
    public string? BackupPaths { get; init; }

    public DateTime DownloadedAtLocal => DownloadedAtUtc.ToLocalTime();

    public FileIdentity Identity =>
        new(FileName, DateTime.UnixEpoch.AddSeconds(MTime), Size);
}

/// <summary>Whether the copy a history row points at is still where it was put.</summary>
public enum DestinationStatus
{
    /// <summary>No history row, so there is nothing to check.</summary>
    NotApplicable,

    /// <summary>The downloaded copy is present.</summary>
    Present,

    /// <summary>The downloaded copy is gone: deleted, renamed, or moved elsewhere.</summary>
    Missing,

    /// <summary>
    /// The volume holding the copy is not mounted, so its absence proves nothing. Treated as
    /// still downloaded rather than as deleted.
    /// </summary>
    Offline,
}

/// <summary>
/// Result of asking the history whether a file has been downloaded before, and whether the
/// copy it produced is still on disk.
/// </summary>
public readonly record struct HistoryLookup(DownloadRecord? Record, DestinationStatus Destination)
{
    public static readonly HistoryLookup NotFound = new(null, DestinationStatus.NotApplicable);

    public bool IsPreviouslyDownloaded => Record is not null;

    public DownloadState ToState() => Record is null
        ? DownloadState.New
        : Destination == DestinationStatus.Missing
            ? DownloadState.MissingAtDestination
            : DownloadState.PreviouslyDownloaded;
}
