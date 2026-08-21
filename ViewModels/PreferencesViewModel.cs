using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SparkPhotoDownloader.Models;
using SparkPhotoDownloader.Services;

namespace SparkPhotoDownloader.ViewModels;

/// <summary>Backs the Preferences window. Changes are applied as they are made.</summary>
public sealed partial class PreferencesViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly SettingsService _settingsService;
    private readonly bool _initializing = true;

    public PreferencesViewModel(MainViewModel main)
    {
        _main = main;
        _settingsService = main.SettingsService;

        var s = Settings;
        ScanDevicesAutomatically = s.ScanDevicesAutomatically;
        OnlyExternalDevicesWithCameraFolder = s.OnlyExternalDevicesWithCameraFolder;
        IncludeFixedDrives = s.IncludeFixedDrives;
        StartDownloadOnDeviceInsertion = s.StartDownloadOnDeviceInsertion;
        EjectAfterDownload = s.EjectAfterDownload;
        AutoUncheckPreviouslyDownloaded = s.AutoUncheckPreviouslyDownloaded;
        RedownloadWhenMissingAtDestination = s.RedownloadWhenMissingAtDestination;
        VerifyCopies = s.VerifyCopies;
        GenerateThumbnails = s.GenerateThumbnails;
        UseFfmpegForVideoThumbnails = s.UseFfmpegForVideoThumbnails;
        ConflictResolution = s.ConflictResolution;

        RefreshCacheInfo();
        RefreshHistoryInfo();

        _initializing = false;
    }

    public AppSettings Settings => _settingsService.Settings;

    public IReadOnlyList<ConflictResolution> ConflictOptions { get; } =
        Enum.GetValues<ConflictResolution>();

    public string DataDirectory => AppPaths.DataDirectory;

    public bool FfmpegAvailable => _main.Thumbnails.FfmpegAvailable;

    public string FfmpegStatus => FfmpegAvailable
        ? "ffmpeg was found, so video frames can be used as previews."
        : "ffmpeg was not found on PATH. Videos fall back to a THM sidecar or an embedded " +
          "poster frame; install ffmpeg for a preview of any clip.";

    // ---- Devices -------------------------------------------------------------------------

    [ObservableProperty]
    public partial bool ScanDevicesAutomatically { get; set; }

    [ObservableProperty]
    public partial bool OnlyExternalDevicesWithCameraFolder { get; set; }

    [ObservableProperty]
    public partial bool IncludeFixedDrives { get; set; }

    [ObservableProperty]
    public partial bool StartDownloadOnDeviceInsertion { get; set; }

    [ObservableProperty]
    public partial bool EjectAfterDownload { get; set; }

    // ---- Downloading ---------------------------------------------------------------------

    [ObservableProperty]
    public partial bool AutoUncheckPreviouslyDownloaded { get; set; }

    [ObservableProperty]
    public partial bool RedownloadWhenMissingAtDestination { get; set; }

    [ObservableProperty]
    public partial bool VerifyCopies { get; set; }

    [ObservableProperty]
    public partial ConflictResolution ConflictResolution { get; set; }

    // ---- Thumbnails ----------------------------------------------------------------------

    [ObservableProperty]
    public partial bool GenerateThumbnails { get; set; }

    [ObservableProperty]
    public partial bool UseFfmpegForVideoThumbnails { get; set; }

    [ObservableProperty]
    public partial string CacheInfo { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HistoryInfo { get; set; } = string.Empty;

    // ---- Commands ------------------------------------------------------------------------

    [RelayCommand]
    private void ClearThumbnailCache()
    {
        var (files, bytes) = _main.Thumbnails.ClearCache();
        CacheInfo = $"Cleared {files:N0} cached thumbnails ({ThumbnailViewModel.FormatBytes(bytes)}).";
    }

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        var confirmed = await _main.Ui.ConfirmAsync(
            "Clear the download history",
            $"This erases the record of all {_main.History.Count:N0} files this program has " +
            "downloaded. Every file on every card will then look new and will be offered for " +
            "download again.\n\nThe downloaded copies themselves are not touched.",
            "Clear history");

        if (!confirmed)
            return;

        _main.History.Clear();
        RefreshHistoryInfo();
        await _main.RecheckDestinationsCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private async Task ForgetMissingAsync()
    {
        var removed = await Task.Run(_main.History.ForgetMissing);
        RefreshHistoryInfo();
        HistoryInfo += $"  ·  removed {removed:N0} row(s) whose copies were gone.";
        await _main.RecheckDestinationsCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void OpenDataFolder() => _main.Ui.RevealInFileManager(AppPaths.DataDirectory);

    private void RefreshCacheInfo()
    {
        var (files, bytes) = _main.Thumbnails.CacheSize();
        CacheInfo = files == 0
            ? "The thumbnail cache is empty."
            : $"{files:N0} cached thumbnails, {ThumbnailViewModel.FormatBytes(bytes)}.";
    }

    private void RefreshHistoryInfo() =>
        HistoryInfo = $"{_main.History.Count:N0} files recorded as downloaded.";

    // ---- Persistence ---------------------------------------------------------------------

    private void Apply(Action<AppSettings> change)
    {
        if (_initializing)
            return;

        change(Settings);
        _settingsService.Save();
    }

    partial void OnScanDevicesAutomaticallyChanged(bool value) =>
        Apply(s => s.ScanDevicesAutomatically = value);

    partial void OnOnlyExternalDevicesWithCameraFolderChanged(bool value) =>
        Apply(s => s.OnlyExternalDevicesWithCameraFolder = value);

    partial void OnIncludeFixedDrivesChanged(bool value)
    {
        Apply(s => s.IncludeFixedDrives = value);

        if (!_initializing)
            _ = _main.RefreshDevicesCommand.ExecuteAsync(null);
    }

    partial void OnStartDownloadOnDeviceInsertionChanged(bool value) =>
        Apply(s => s.StartDownloadOnDeviceInsertion = value);

    partial void OnEjectAfterDownloadChanged(bool value) =>
        Apply(s => s.EjectAfterDownload = value);

    partial void OnAutoUncheckPreviouslyDownloadedChanged(bool value) =>
        Apply(s => s.AutoUncheckPreviouslyDownloaded = value);

    partial void OnRedownloadWhenMissingAtDestinationChanged(bool value) =>
        Apply(s => s.RedownloadWhenMissingAtDestination = value);

    partial void OnVerifyCopiesChanged(bool value) => Apply(s => s.VerifyCopies = value);

    partial void OnConflictResolutionChanged(ConflictResolution value) =>
        Apply(s => s.ConflictResolution = value);

    partial void OnGenerateThumbnailsChanged(bool value) => Apply(s => s.GenerateThumbnails = value);

    partial void OnUseFfmpegForVideoThumbnailsChanged(bool value) =>
        Apply(s => s.UseFfmpegForVideoThumbnails = value);
}
