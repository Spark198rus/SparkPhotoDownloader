namespace SparkPhotoDownloader.Models;

/// <summary>
/// One file found on a download source. Immutable scan result; mutable UI state lives on
/// the corresponding view model.
/// </summary>
public sealed class SourceFile
{
    public required string FullPath { get; init; }
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
