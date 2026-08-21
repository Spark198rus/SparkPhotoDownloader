namespace SparkPhotoDownloader.Models;

/// <summary>
/// Everything the program remembers between runs, other than the download history.
/// Persisted as JSON next to the history database.
/// </summary>
public sealed class AppSettings
{
    // ---- Destinations -------------------------------------------------------------------
    public string? PhotoDestination { get; set; }
    public string? VideoDestination { get; set; }

    public string PhotoSubfolderPreset { get; set; } = "Date (YYYY/YYYYMMDD)";
    public string VideoSubfolderPreset { get; set; } = "Date (YYYY/YYYYMMDD)";
    public string PhotoRenamePreset { get; set; } = "Original Filename";
    public string VideoRenamePreset { get; set; } = "Original Filename";

    public List<NamePreset> CustomPhotoSubfolderPresets { get; set; } = [];
    public List<NamePreset> CustomVideoSubfolderPresets { get; set; } = [];
    public List<NamePreset> CustomPhotoRenamePresets { get; set; } = [];
    public List<NamePreset> CustomVideoRenamePresets { get; set; } = [];

    // ---- Sources ------------------------------------------------------------------------
    /// <summary>Folders added by hand through the "This Computer" panel.</summary>
    public List<string> ComputerSources { get; set; } = [];

    /// <summary>Scan removable volumes automatically as they appear.</summary>
    public bool ScanDevicesAutomatically { get; set; } = true;

    /// <summary>Only treat a volume as a device when it contains a DCIM-style folder.</summary>
    public bool OnlyExternalDevicesWithCameraFolder { get; set; } = true;

    public bool IncludeFixedDrives { get; set; }

    /// <summary>Volume root paths the user has told the program to leave alone.</summary>
    public List<string> IgnoredDevices { get; set; } = [];

    // ---- Behaviour ----------------------------------------------------------------------
    public TransferMode TransferMode { get; set; } = TransferMode.Copy;
    public ConflictResolution ConflictResolution { get; set; } = ConflictResolution.AddUniqueIdentifier;

    /// <summary>Verify each copy by comparing a hash of source and destination.</summary>
    public bool VerifyCopies { get; set; }

    /// <summary>Leave files already in the history unchecked when a scan completes.</summary>
    public bool AutoUncheckPreviouslyDownloaded { get; set; } = true;

    /// <summary>
    /// When a file was downloaded but its copy is gone from the destination, download it
    /// again instead of only flagging it. Off by default: remember it, do not fetch it
    /// again, mark it.
    /// </summary>
    public bool RedownloadWhenMissingAtDestination { get; set; }

    public bool StartDownloadOnDeviceInsertion { get; set; }
    public bool EjectAfterDownload { get; set; }
    public bool GenerateThumbnails { get; set; } = true;
    public bool UseFfmpegForVideoThumbnails { get; set; } = true;

    // ---- Job codes ----------------------------------------------------------------------
    public List<string> JobCodes { get; set; } = [];
    public string? LastJobCode { get; set; }

    // ---- Backup -------------------------------------------------------------------------
    public bool BackupEnabled { get; set; }
    public string? PhotoBackupPath { get; set; }
    public string? VideoBackupPath { get; set; }

    // ---- View ---------------------------------------------------------------------------
    public ThumbnailSort Sort { get; set; } = ThumbnailSort.ModificationTime;
    public SortDirection SortDirection { get; set; } = SortDirection.Descending;
    public ThumbnailFilter Filter { get; set; } = ThumbnailFilter.All;
    public int ThumbnailSize { get; set; } = 160;

    /// <summary>Timeline grouping threshold, in minutes.</summary>
    public int ProximityMinutes { get; set; } = 60;

    public bool ShowSourcePanel { get; set; } = true;
    public bool ShowDestinationPanel { get; set; } = true;
    public double WindowWidth { get; set; } = 1500;
    public double WindowHeight { get; set; } = 950;

    // ---- Sequences ----------------------------------------------------------------------
    public int StoredSequenceNumber { get; set; } = 1;
    public int DownloadsToday { get; set; }
    public DateTime DownloadsTodayDate { get; set; } = DateTime.Today;
}
