namespace SparkPhotoDownloader.Models;

/// <summary>Broad classification of a scanned file.</summary>
public enum FileKind
{
    Photo,
    Video,
    Audio,
    Sidecar,
    Unknown,
}

/// <summary>
/// Lifecycle state of a scanned file, as shown on its thumbnail.
/// </summary>
public enum DownloadState
{
    /// <summary>Never seen before by this program.</summary>
    New,

    /// <summary>Downloaded before, and the copy is still present at the destination.</summary>
    PreviouslyDownloaded,

    /// <summary>
    /// Downloaded before, but the copy that was written is no longer at the destination.
    /// Deliberately *not* re-downloaded: it is flagged instead.
    /// </summary>
    MissingAtDestination,

    /// <summary>Currently being copied.</summary>
    Downloading,

    /// <summary>Copied during this session.</summary>
    Downloaded,

    /// <summary>The copy failed during this session.</summary>
    Failed,
}

/// <summary>How the thumbnail grid is ordered.</summary>
public enum ThumbnailSort
{
    ModificationTime,
    CaptureTime,
    Checked,
    FileName,
    Extension,
    FileType,
    Device,
    Size,
}

/// <summary>Which files the thumbnail grid shows.</summary>
public enum ThumbnailFilter
{
    All,
    New,
    PreviouslyDownloaded,
    MissingAtDestination,
    PhotosOnly,
    VideosOnly,
}

public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>Whether downloading copies files or moves them off the source.</summary>
public enum TransferMode
{
    Copy,
    Move,
}

/// <summary>What to do when the generated destination path is already taken.</summary>
public enum ConflictResolution
{
    /// <summary>Append _1, _2 ... until the name is free.</summary>
    AddUniqueIdentifier,

    /// <summary>Leave the existing file alone and skip the source file.</summary>
    SkipDownload,

    /// <summary>Replace the file that is already there.</summary>
    Overwrite,
}

public enum DeviceKind
{
    Camera,
    MemoryCard,
    Phone,
    ExternalDrive,
    LocalFolder,
    Backup,
}

/// <summary>The source of a date used by a naming token.</summary>
public enum DateSource
{
    CaptureDate,
    ModificationDate,
    Today,
    Yesterday,
    DownloadTime,
}

public enum TextCase
{
    Original,
    Upper,
    Lower,
}

/// <summary>Every substitution that can appear in a generated folder or file name.</summary>
public enum NameToken
{
    Text,
    Separator,
    Date,
    OriginalName,
    ImageNumber,
    Extension,
    Aperture,
    Iso,
    ExposureTime,
    FocalLength,
    CameraMake,
    CameraModel,
    ShortCameraModel,
    SerialNumber,
    ShutterCount,
    LensModel,
    Width,
    Height,
    Codec,
    FrameRate,
    Duration,
    DownloadsToday,
    SessionSequence,
    StoredSequence,
    SubfolderSequence,
    SequenceLetter,
    JobCode,
    DeviceName,
}
