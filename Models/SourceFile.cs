namespace SparkPhotoDownloader.Models;

/// <summary>
/// One file found on a download source. Immutable scan result; mutable UI state lives on
/// the corresponding view model.
/// </summary>
public sealed class SourceFile
{
    /// <summary>
    /// Where the file is. For a file on a phone or camera connected over MTP/PTP this is a
    /// display path ("Pixel 8\Internal shared storage\DCIM\Camera\IMG_1.jpg") that is
    /// unique but cannot be opened; read any file through Services.SourceAccess.
    /// </summary>
    public required string FullPath { get; init; }

    /// <summary>The portable device the file is on, or null for a file on a mounted volume.</summary>
    public string? DeviceId { get; init; }

    /// <summary>The file's path on <see cref="DeviceId"/>, as the device names it.</summary>
    public string? DevicePath { get; init; }

    public bool IsOnDevice => DeviceId is not null;
    public required string FileName { get; init; }

    /// <summary>Lower-case extension without the dot.</summary>
    public required string Extension { get; init; }

    public required long Size { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public required FileKind Kind { get; init; }

    /// <summary>Identifier of the <see cref="DownloadSource"/> this file was scanned from.</summary>
    public required string SourceId { get; init; }

    public required string SourceName { get; init; }

    /// <summary>Path of the containing folder relative to the source root.</summary>
    public required string RelativeFolder { get; init; }

    /// <summary>
    /// Companion files (XMP/THM/WAV) that travel with this file and are copied alongside it.
    /// </summary>
    public List<string> Sidecars { get; } = [];

    /// <summary>
    /// Stable key used by the download history. Matches Rapid Photo Downloader's scheme of
    /// (file name, modification time, size), which survives the file being copied or renamed
    /// at the destination.
    /// </summary>
    public FileIdentity Identity => new(FileName, ModifiedUtc, Size);

    public override string ToString() => FullPath;
}

/// <summary>
/// The tuple that identifies a source file across sessions: name + mtime + size.
/// </summary>
public readonly record struct FileIdentity(string FileName, DateTime ModifiedUtc, long Size)
{
    /// <summary>Modification time as whole seconds since the Unix epoch, the form stored in SQLite.</summary>
    public double MTime => Math.Round((ModifiedUtc - DateTime.UnixEpoch).TotalSeconds, 3);
}
