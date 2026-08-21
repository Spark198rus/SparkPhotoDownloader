namespace SparkPhotoDownloader.Models;

/// <summary>
/// A place files are scanned from: a memory card, a camera volume, an external drive, or a
/// plain folder added through "This Computer".
/// </summary>
public sealed class DownloadSource
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string RootPath { get; init; }
    public required DeviceKind Kind { get; init; }

    /// <summary>True when a DCIM (or similar) folder marks this volume as camera media.</summary>
    public bool HasCameraFolder { get; init; }

    /// <summary>Removable volumes can be ejected once a download finishes.</summary>
    public bool IsRemovable { get; init; }

    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }

    /// <summary>Volume label or file-system type, shown under the device name.</summary>
    public string? Description { get; init; }

    public long UsedBytes => Math.Max(0, TotalBytes - FreeBytes);

    public double UsedFraction => TotalBytes > 0 ? (double)UsedBytes / TotalBytes : 0;
}
