using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SparkPhotoDownloader.Models;
using SparkPhotoDownloader.Services;

namespace SparkPhotoDownloader.ViewModels;

/// <summary>
/// Drives the main window: sources on the left, thumbnails in the middle, destinations on
/// the right, and the download run that ties them together.
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly DownloadHistoryService _history;
    private readonly MetadataService _metadata;
    private readonly ThumbnailService _thumbnails;
    private readonly DeviceScanner _deviceScanner;
    private readonly FileScanner _fileScanner;
    private readonly DownloadEngine _engine;
    private readonly DispatcherTimer _deviceWatcher;

    private readonly List<ThumbnailViewModel> _all = [];
    private CancellationTokenSource? _scanCancellation;
    private CancellationTokenSource? _downloadCancellation;

    /// <summary>Set while checking many files at once, so totals are recomputed just once.</summary>
    private bool _suppressRecount;

    /// <summary>Guards against overlapping device enumerations from the poll timer.</summary>
    private bool _deviceScanInFlight;

    /// <summary>How many modal dialogs are open. The device poll stands down while any are.</summary>
    private int _dialogDepth;

    /// <summary>
    /// True while the constructor seeds view state from settings. The property change
    /// handlers below write settings back and rebuild the grid, neither of which should run
    /// before there is anything to rebuild.
    /// </summary>
    private readonly bool _initializing = true;

    public MainViewModel(IUiServices ui)
    {
        Ui = ui;

        AppPaths.EnsureCreated();
        _settingsService = new SettingsService();
        _history = new DownloadHistoryService(AppPaths.HistoryDatabase);
        _metadata = new MetadataService();
        _thumbnails = new ThumbnailService(Settings);
        _deviceScanner = new DeviceScanner(Settings);
        _fileScanner = new FileScanner();
        _engine = new DownloadEngine(_metadata, _history, _settingsService);

        PhotoPanel = new DestinationPanelViewModel(FileKind.Photo, _settingsService, OnDestinationChanged);
        VideoPanel = new DestinationPanelViewModel(FileKind.Video, _settingsService, OnDestinationChanged);

        foreach (var code in Settings.JobCodes)
            JobCodes.Add(code);
        SelectedJobCode = Settings.LastJobCode;

        Sort = Settings.Sort;
        SortDirection = Settings.SortDirection;
        Filter = Settings.Filter;
        ThumbnailSize = Settings.ThumbnailSize;
        ProximityMinutes = Settings.ProximityMinutes;
        BackupEnabled = Settings.BackupEnabled;
        PhotoBackupPath = Settings.PhotoBackupPath ?? string.Empty;
        VideoBackupPath = Settings.VideoBackupPath ?? string.Empty;
        TransferMode = Settings.TransferMode;

        _initializing = false;

        // Devices come and go while the program is open; poll rather than depend on a
        // platform-specific notification API.
        _deviceWatcher = new DispatcherTimer(
            TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => WatchDevices());

        StatusText = _history.Count > 0
            ? $"{_history.Count:N0} files in the download history"
            : "Ready";
    }

    public IUiServices Ui { get; }

    public AppSettings Settings => _settingsService.Settings;

    public SettingsService SettingsService => _settingsService;

    public DownloadHistoryService History => _history;

    public ThumbnailService Thumbnails => _thumbnails;

    // ---- Collections ---------------------------------------------------------------------

    /// <summary>Volumes found by the device scanner.</summary>
    public ObservableCollection<SourceViewModel> Devices { get; } = [];

    /// <summary>Folders the user added through the This Computer panel.</summary>
    public ObservableCollection<SourceViewModel> ComputerSources { get; } = [];

    /// <summary>The thumbnails currently shown, after sorting and filtering.</summary>
    public ObservableCollection<ThumbnailViewModel> Files { get; } = [];

    public ObservableCollection<TimelineGroupViewModel> Timeline { get; } = [];

    public ObservableCollection<string> JobCodes { get; } = [];

    public DestinationPanelViewModel PhotoPanel { get; }

    public DestinationPanelViewModel VideoPanel { get; }

    public IReadOnlyList<ThumbnailSort> SortOptions { get; } = Enum.GetValues<ThumbnailSort>();

    public IReadOnlyList<ThumbnailFilter> FilterOptions { get; } = Enum.GetValues<ThumbnailFilter>();

    public IReadOnlyList<int> ProximityOptions { get; } = ProximityService.Thresholds;

    public IReadOnlyList<TransferMode> TransferModes { get; } = Enum.GetValues<TransferMode>();

    /// <summary>
    /// The Timeline slider's position. It moves through the fixed list of thresholds rather
    /// than over raw minutes, so every stop is a round number.
    /// </summary>
    public int ProximityIndex
    {
        get
        {
            var index = Array.IndexOf(ProximityService.Thresholds, ProximityMinutes);
            return index < 0 ? Array.IndexOf(ProximityService.Thresholds, 60) : index;
        }
        set
        {
            var clamped = Math.Clamp(value, 0, ProximityService.Thresholds.Length - 1);
            var minutes = ProximityService.Thresholds[clamped];
            if (minutes == ProximityMinutes)
                return;

            ProximityMinutes = minutes;
            OnPropertyChanged();
        }
    }

    // ---- View state ----------------------------------------------------------------------

    [ObservableProperty]
    public partial string StatusText { get; set; } = "Ready";

    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [NotifyCanExecuteChangedFor(nameof(CancelDownloadCommand))]
    [ObservableProperty]
    public partial bool IsDownloading { get; set; }

    public bool IsIdle => !IsScanning && !IsDownloading;

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial bool IsProgressIndeterminate { get; set; }

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [NotifyPropertyChangedFor(nameof(ShowsFilteredNotice))]
    [ObservableProperty]
    public partial ThumbnailFilter Filter { get; set; }

    [ObservableProperty]
    public partial ThumbnailSort Sort { get; set; }

    [ObservableProperty]
    public partial SortDirection SortDirection { get; set; }

    [ObservableProperty]
    public partial int ThumbnailSize { get; set; } = 160;

    [ObservableProperty]
    public partial int ProximityMinutes { get; set; } = 60;

    [ObservableProperty]
    public partial TransferMode TransferMode { get; set; }

    [ObservableProperty]
    public partial bool BackupEnabled { get; set; }

    [ObservableProperty]
    public partial string PhotoBackupPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string VideoBackupPath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? SelectedJobCode { get; set; }

    [ObservableProperty]
    public partial ThumbnailViewModel? SelectedFile { get; set; }

    public string ProximityLabel => ProximityService.DescribeThreshold(ProximityMinutes);

    public bool ShowsFilteredNotice => Filter != ThumbnailFilter.All || Timeline.Any(t => t.IsSelected);

    // ---- Counts --------------------------------------------------------------------------

    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    [NotifyCanExecuteChangedFor(nameof(DownloadCommand))]
    [ObservableProperty]
    public partial int CheckedCount { get; set; }

    [NotifyPropertyChangedFor(nameof(SelectionSummary))]
    [ObservableProperty]
    public partial long CheckedBytes { get; set; }

    [NotifyPropertyChangedFor(nameof(LibrarySummary))]
    [ObservableProperty]
    public partial int TotalCount { get; set; }

    [NotifyPropertyChangedFor(nameof(LibrarySummary))]
    [ObservableProperty]
    public partial int NewCount { get; set; }

    [NotifyPropertyChangedFor(nameof(LibrarySummary))]
    [ObservableProperty]
    public partial int PreviouslyDownloadedCount { get; set; }

    /// <summary>
    /// Files this program downloaded whose copies are no longer at the destination. This is
    /// the number the flagging behaviour exists to surface.
    /// </summary>
    [NotifyPropertyChangedFor(nameof(LibrarySummary))]
    [NotifyPropertyChangedFor(nameof(HasMissing))]
    [ObservableProperty]
    public partial int MissingCount { get; set; }

    public bool HasMissing => MissingCount > 0;

    public string SelectionSummary => CheckedCount == 0
        ? "Nothing selected"
        : $"{CheckedCount:N0} file{(CheckedCount == 1 ? "" : "s")} selected  ·  " +
          ThumbnailViewModel.FormatBytes(CheckedBytes);

    public string LibrarySummary
    {
        get
        {
            if (TotalCount == 0)
                return "No files";

            var parts = new List<string> { $"{TotalCount:N0} files" };
            if (NewCount > 0)
                parts.Add($"{NewCount:N0} new");
            if (PreviouslyDownloadedCount > 0)
                parts.Add($"{PreviouslyDownloadedCount:N0} already downloaded");
            if (MissingCount > 0)
                parts.Add($"{MissingCount:N0} missing at destination");

            return string.Join("  ·  ", parts);
        }
    }

    // ---- Lifetime ------------------------------------------------------------------------

    /// <summary>Called once the window is up: find devices and scan whatever is already there.</summary>
    public async Task InitializeAsync()
    {
        foreach (var path in Settings.ComputerSources.ToList())
        {
            if (Directory.Exists(path))
                ComputerSources.Add(new SourceViewModel(
                    DeviceScanner.CreateFolderSource(path), OnSourceCheckedChanged) { Owner = this });
        }

        await RefreshDevicesAsync();
        _deviceWatcher.Start();

        if (Settings.ScanDevicesAutomatically && (Devices.Count > 0 || ComputerSources.Count > 0))
            await ScanAllAsync();
    }

    public void Dispose()
    {
        _deviceWatcher.Stop();
        _scanCancellation?.Cancel();
        _downloadCancellation?.Cancel();
        _history.Dispose();
        _settingsService.Save();
    }

    // ---- Devices -------------------------------------------------------------------------

    /// <summary>
    /// Look for connected volumes. Enumeration touches every drive, which can block for
    /// seconds on a disconnected network share, so it never runs on the UI thread.
    /// </summary>
    [RelayCommand]
    private async Task RefreshDevicesAsync()
    {
        if (_deviceScanInFlight)
            return;

        _deviceScanInFlight = true;
        try
        {
            var found = await Task.Run(_deviceScanner.ScanDevices);
            MergeDevices(found);
        }
        catch (Exception ex)
        {
            StatusText = "Could not list devices: " + ex.Message;
        }
        finally
        {
            _deviceScanInFlight = false;
        }
    }

    private void MergeDevices(IReadOnlyList<DownloadSource> found)
    {
        var seen = new HashSet<string>(found.Select(f => f.Id), StringComparer.OrdinalIgnoreCase);

        for (var i = Devices.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Devices[i].Id))
            {
                RemoveFilesFromSource(Devices[i].Id);
                Devices.RemoveAt(i);
            }
        }

        foreach (var source in found)
        {
            if (Devices.Any(d => string.Equals(d.Id, source.Id, StringComparison.OrdinalIgnoreCase)))
                continue;

            Devices.Add(new SourceViewModel(source, OnSourceCheckedChanged) { Owner = this });
        }
    }

    /// <summary>Polls for volumes appearing or disappearing and reacts to both.</summary>
    private async void WatchDevices()
    {
        if (!Settings.ScanDevicesAutomatically || IsScanning || IsDownloading)
            return;

        // A modal dialog owns the user's attention; rescanning behind it would only fight
        // with whatever they are doing there.
        if (_dialogDepth > 0 || _deviceScanInFlight)
            return;

        try
        {
            var before = Devices.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            await RefreshDevicesAsync();
            var added = Devices.Where(d => !before.Contains(d.Id)).ToList();

            if (added.Count == 0)
                return;

            StatusText = added.Count == 1
                ? $"{added[0].Name} connected"
                : $"{added.Count} devices connected";

            foreach (var device in added)
                await ScanSourceAsync(device);

            if (Settings.StartDownloadOnDeviceInsertion && CheckedCount > 0)
                await DownloadAsync();
        }
        catch (Exception ex)
        {
            StatusText = "Device watch failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task AddComputerSourceAsync()
    {
        var path = await Ui.PickFolderAsync("Choose a folder to download from");
        if (path is null)
            return;

        if (ComputerSources.Any(s => string.Equals(s.RootPath, path, StringComparison.OrdinalIgnoreCase)))
        {
            await Ui.ShowMessageAsync("Already added", "That folder is already a download source.");
            return;
        }

        var vm = new SourceViewModel(DeviceScanner.CreateFolderSource(path), OnSourceCheckedChanged)
            { Owner = this };
        ComputerSources.Add(vm);

        Settings.ComputerSources.Add(path);
        _settingsService.Save();

        await ScanSourceAsync(vm);
    }

    [RelayCommand]
    private void RemoveComputerSource(SourceViewModel? source)
    {
        if (source is null)
            return;

        ComputerSources.Remove(source);
        Settings.ComputerSources.RemoveAll(
            p => string.Equals(p, source.RootPath, StringComparison.OrdinalIgnoreCase));
        _settingsService.Save();

        RemoveFilesFromSource(source.Id);
        Rebuild();
    }

    [RelayCommand]
    private void RevealSource(SourceViewModel? source)
    {
        if (source is not null)
            Ui.RevealInFileManager(source.RootPath);
    }

    private void OnSourceCheckedChanged(SourceViewModel source)
    {
        _suppressRecount = true;
        try
        {
            foreach (var file in _all.Where(f => f.File.SourceId == source.Id))
            {
                if (!source.IsChecked)
                {
                    file.IsChecked = false;
                    continue;
                }

                // Checking a source never re-checks files that are already downloaded: that
                // decision stays with the user.
                file.IsChecked = ShouldCheckByDefault(file);
            }
        }
        finally
        {
            _suppressRecount = false;
        }

        RecountChecked();
    }

    // ---- Scanning ------------------------------------------------------------------------

    [RelayCommand]
    private async Task ScanAllAsync()
    {
        foreach (var source in AllSources().ToList())
            await ScanSourceAsync(source);
    }

    [RelayCommand]
    private async Task RescanAsync()
    {
        _all.Clear();
        Files.Clear();
        Timeline.Clear();

        foreach (var source in AllSources())
            source.ResetCounts();

        _history.InvalidateVolumeCache();
        await ScanAllAsync();
    }

    private IEnumerable<SourceViewModel> AllSources() => Devices.Concat(ComputerSources);

    private async Task ScanSourceAsync(SourceViewModel source)
    {
        _scanCancellation?.Cancel();
        _scanCancellation = new CancellationTokenSource();
        var token = _scanCancellation.Token;

        IsScanning = true;
        IsProgressIndeterminate = true;
        source.IsScanning = true;
        RemoveFilesFromSource(source.Id);

        var progress = new Progress<ScanProgress>(p =>
        {
            source.FileCount = p.FilesFound;
            source.ScanDetail = p.CurrentFolder;
            StatusText = $"Scanning {p.SourceName}… {p.FilesFound:N0} files";
        });

        try
        {
            var found = await Task.Run(
                () => _fileScanner.Scan(source.Source, progress, token), token);

            // Metadata and history lookups are the slow part; do them off the UI thread.
            var built = await Task.Run(() => found
                .Select(file =>
                {
                    token.ThrowIfCancellationRequested();
                    var lookup = _history.Lookup(file.Identity);
                    var date = _metadata.EffectiveDate(file);
                    return (file, lookup, date);
                })
                .ToList(), token);

            foreach (var (file, lookup, date) in built)
            {
                var vm = new ThumbnailViewModel(file, lookup, date, OnFileCheckedChanged) { Owner = this };
                vm.IsChecked = source.IsChecked && ShouldCheckByDefault(vm);
                _all.Add(vm);
            }

            source.PhotoCount = built.Count(b => b.file.Kind == FileKind.Photo);
            source.VideoCount = built.Count(b => b.file.Kind == FileKind.Video);
            source.FileCount = built.Count;

            Rebuild();
            StatusText = $"{source.Name}: {built.Count:N0} files found";

            _ = LoadThumbnailsAsync(token);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled";
        }
        catch (Exception ex)
        {
            StatusText = $"Scan of {source.Name} failed: {ex.Message}";
        }
        finally
        {
            source.IsScanning = false;
            source.ScanDetail = null;
            IsScanning = false;
            IsProgressIndeterminate = false;
        }
    }

    /// <summary>
    /// A file is checked by default when it is new. Files already in the history stay
    /// unchecked, including the ones whose copies have gone missing: they are flagged, not
    /// silently fetched again.
    /// </summary>
    private bool ShouldCheckByDefault(ThumbnailViewModel file)
    {
        if (!file.WasDownloadedBefore)
            return true;

        if (!Settings.AutoUncheckPreviouslyDownloaded)
            return true;

        return file.IsMissingAtDestination && Settings.RedownloadWhenMissingAtDestination;
    }

    private void RemoveFilesFromSource(string sourceId)
    {
        _all.RemoveAll(f => string.Equals(f.File.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
    }

    // ---- Thumbnails ----------------------------------------------------------------------

    private async Task LoadThumbnailsAsync(CancellationToken token)
    {
        if (!Settings.GenerateThumbnails)
            return;

        var size = ThumbnailSize;

        // Build previews in the order the grid shows them, not the order the file system
        // handed the files over. Otherwise a card sorted newest-first spends the whole scan
        // filling in thumbnails the user cannot see while the top row stays blank.
        var displayOrder = new Dictionary<ThumbnailViewModel, int>(Files.Count);
        for (var i = 0; i < Files.Count; i++)
            displayOrder[Files[i]] = i;

        var pending = _all
            .Where(f => f.Thumbnail is null && !f.ThumbnailFailed)
            .OrderBy(f => displayOrder.TryGetValue(f, out var index) ? index : int.MaxValue)
            .ToList();

        if (pending.Count == 0)
            return;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2),
            CancellationToken = token,
        };

        try
        {
            await Parallel.ForEachAsync(pending, options, async (item, cancellation) =>
            {
                var result = await Task.Run(
                    () => _thumbnails.Load(item.File, size, cancellation), cancellation);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (result.Bitmap is null)
                    {
                        item.ThumbnailFailed = true;
                        return;
                    }

                    if (result.NeedsOrientation)
                    {
                        // Rotation renders through the platform's drawing stack, so it has to
                        // happen here, and the upright image is what gets cached.
                        var upright = ThumbnailService.ApplyOrientation(result.Bitmap, result.Orientation);
                        _thumbnails.Cache(upright, result.CachePath);
                        item.Thumbnail = upright;
                    }
                    else
                    {
                        item.Thumbnail = result.Bitmap;
                    }
                });
            });
        }
        catch (OperationCanceledException)
        {
            // A new scan superseded this one.
        }
        catch (Exception ex)
        {
            StatusText = "Thumbnail generation failed: " + ex.Message;
        }
    }

    // ---- Filtering, sorting, totals ------------------------------------------------------

    /// <summary>Recompute the visible grid, the Timeline, the counts and the destination previews.</summary>
    private void Rebuild()
    {
        RebuildTimeline();
        RebuildVisible();
        RecountStates();
        RecountChecked();
    }

    private void RebuildVisible()
    {
        var selectedGroups = Timeline.Where(t => t.IsSelected).ToList();

        IEnumerable<ThumbnailViewModel> query = _all;

        query = Filter switch
        {
            ThumbnailFilter.New => query.Where(f => !f.WasDownloadedBefore),
            ThumbnailFilter.PreviouslyDownloaded => query.Where(f => f.WasDownloadedBefore),
            ThumbnailFilter.MissingAtDestination => query.Where(f => f.IsMissingAtDestination),
            ThumbnailFilter.PhotosOnly => query.Where(f => f.Kind == FileKind.Photo),
            ThumbnailFilter.VideosOnly => query.Where(f => f.Kind == FileKind.Video),
            _ => query,
        };

        if (selectedGroups.Count > 0)
            query = query.Where(f => selectedGroups.Any(g => g.Paths.Contains(f.File.FullPath)));

        Comparison<ThumbnailViewModel> comparison = Sort switch
        {
            ThumbnailSort.CaptureTime => (a, b) => a.EffectiveDate.CompareTo(b.EffectiveDate),
            ThumbnailSort.Checked => (a, b) => a.IsChecked.CompareTo(b.IsChecked),
            ThumbnailSort.FileName => (a, b) =>
                string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase),
            ThumbnailSort.Extension => (a, b) =>
                string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            ThumbnailSort.FileType => (a, b) => a.Kind.CompareTo(b.Kind),
            ThumbnailSort.Device => (a, b) =>
                string.Compare(a.SourceName, b.SourceName, StringComparison.OrdinalIgnoreCase),
            ThumbnailSort.Size => (a, b) => a.Size.CompareTo(b.Size),
            _ => (a, b) => a.File.ModifiedUtc.CompareTo(b.File.ModifiedUtc),
        };

        var ordered = query.ToList();
        ordered.Sort((a, b) =>
        {
            var result = comparison(a, b);
            if (result == 0)
                result = string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase);
            return SortDirection == SortDirection.Descending ? -result : result;
        });

        Files.Clear();
        foreach (var item in ordered)
            Files.Add(item);

        OnPropertyChanged(nameof(ShowsFilteredNotice));
    }

    private void RebuildTimeline()
    {
        var selected = Timeline.Where(t => t.IsSelected)
            .Select(t => t.Group.Start)
            .ToHashSet();

        var groups = ProximityService.Group(
            _all.Select(f => (f.File, f.EffectiveDate)), ProximityMinutes);

        Timeline.Clear();
        foreach (var group in groups)
        {
            var vm = new TimelineGroupViewModel(group, OnTimelineSelectionChanged)
            {
                IsSelected = selected.Contains(group.Start),
            };
            Timeline.Add(vm);
        }
    }

    private void OnTimelineSelectionChanged()
    {
        RebuildVisible();
        OnPropertyChanged(nameof(ShowsFilteredNotice));
    }

    private void RecountStates()
    {
        TotalCount = _all.Count;
        NewCount = _all.Count(f => f.State == DownloadState.New);
        PreviouslyDownloadedCount = _all.Count(f => f.State == DownloadState.PreviouslyDownloaded);
        MissingCount = _all.Count(f => f.IsMissingAtDestination);
    }

    private void OnFileCheckedChanged(ThumbnailViewModel file)
    {
        if (_suppressRecount)
            return;

        RecountChecked();
    }

    private void RecountChecked()
    {
        var checkedFiles = _all.Where(f => f.IsChecked).ToList();
        CheckedCount = checkedFiles.Count;
        CheckedBytes = checkedFiles.Sum(f => f.Size);

        UpdateDestinationPreviews(checkedFiles);
    }

    private void OnDestinationChanged() => RecountChecked();

    /// <summary>
    /// Refresh both destination panels: the folder tree a download would produce, an example
    /// of the generated name, and whether the volume has room.
    /// </summary>
    private void UpdateDestinationPreviews(List<ThumbnailViewModel> checkedFiles)
    {
        UpdatePanel(PhotoPanel, checkedFiles.Where(f => f.Kind == FileKind.Photo).ToList());
        UpdatePanel(VideoPanel, checkedFiles.Where(f => f.Kind == FileKind.Video).ToList());
    }

    private void UpdatePanel(DestinationPanelViewModel panel, List<ThumbnailViewModel> files)
    {
        panel.SelectedFileCount = files.Count;
        panel.SelectedBytes = files.Sum(f => f.Size);
        panel.RefreshDriveSpace();

        var planned = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Planning every file would mean reading metadata for thousands of files on every
        // click; a sample is enough to show the shape of the tree.
        foreach (var file in files.Take(400))
        {
            var relative = _engine.PlanRelativeFolder(
                file.File, panel.SelectedSubfolderPreset, SelectedJobCode);
            planned[relative] = planned.GetValueOrDefault(relative) + 1;
        }

        if (files.Count > 400)
        {
            // Scale the sampled counts so the numbers in the tree stay honest.
            var factor = (double)files.Count / 400;
            foreach (var key in planned.Keys.ToList())
                planned[key] = (int)Math.Round(planned[key] * factor);
        }

        panel.BuildTree(planned);

        var example = files.FirstOrDefault()
            ?? _all.FirstOrDefault(f => f.Kind == panel.Kind);

        if (example is not null)
        {
            panel.ExampleFolder = _engine.PlanRelativeFolder(
                example.File, panel.SelectedSubfolderPreset, SelectedJobCode) is { Length: > 0 } folder
                ? folder
                : "(destination root)";
            panel.ExampleName = _engine.PlanFileName(
                example.File, panel.SelectedRenamePreset, SelectedJobCode);
        }
        else
        {
            panel.ExampleFolder = "—";
            panel.ExampleName = "—";
        }

        panel.NameWarning = DescribeNameRisk(panel);
    }

    /// <summary>
    /// Warn when a rename scheme cannot produce unique names, which is how downloads from two
    /// cameras quietly overwrite each other.
    /// </summary>
    private static string? DescribeNameRisk(DestinationPanelViewModel panel)
    {
        var preset = panel.SelectedRenamePreset;
        if (preset is null || preset.Parts.Count == 0)
            return null;

        var hasUniquePart = preset.Parts.Any(p => p.Token is
            NameToken.DownloadsToday or NameToken.SessionSequence or NameToken.StoredSequence or
            NameToken.SubfolderSequence or NameToken.SequenceLetter or NameToken.ImageNumber or
            NameToken.OriginalName or NameToken.SerialNumber or NameToken.ShutterCount);

        var hasTime = preset.Parts.Any(p =>
            p.Token == NameToken.Date &&
            (p.Format?.Contains("HH", StringComparison.Ordinal) ?? false));

        return hasUniquePart || hasTime
            ? null
            : "This scheme may not produce unique names. Add a sequence number or the time.";
    }

    // ---- Selection commands --------------------------------------------------------------

    [RelayCommand]
    private void CheckAll() => SetChecked(Files, true);

    [RelayCommand]
    private void UncheckAll() => SetChecked(Files, false);

    [RelayCommand]
    private void CheckAllPhotos() =>
        SetChecked(Files.Where(f => f.Kind == FileKind.Photo), true);

    [RelayCommand]
    private void CheckAllVideos() =>
        SetChecked(Files.Where(f => f.Kind == FileKind.Video), true);

    [RelayCommand]
    private static void CheckSingle(ThumbnailViewModel? file)
    {
        if (file is not null)
            file.IsChecked = true;
    }

    [RelayCommand]
    private static void UncheckSingle(ThumbnailViewModel? file)
    {
        if (file is not null)
            file.IsChecked = false;
    }

    [RelayCommand]
    private void CheckOnlyNew()
    {
        SetChecked(Files, false);
        SetChecked(Files.Where(f => !f.WasDownloadedBefore), true);
    }

    private void SetChecked(IEnumerable<ThumbnailViewModel> files, bool value)
    {
        _suppressRecount = true;
        try
        {
            foreach (var file in files.ToList())
                file.IsChecked = value;
        }
        finally
        {
            _suppressRecount = false;
        }

        RecountChecked();
    }

    // ---- History commands ----------------------------------------------------------------

    /// <summary>
    /// Drop a file's history row so the next download treats it as new. The deliberate way to
    /// re-fetch something whose copy was deleted.
    /// </summary>
    [RelayCommand]
    private void ForgetFile(ThumbnailViewModel? file)
    {
        if (file is null)
            return;

        _history.Forget(file.File.Identity);
        file.ApplyHistory(HistoryLookup.NotFound);
        file.IsChecked = true;

        RecountStates();
        RecountChecked();
        StatusText = $"{file.FileName} forgotten — it will be downloaded again";
    }

    /// <summary>Forget every file currently shown whose copy has gone missing.</summary>
    [RelayCommand]
    private async Task ForgetAllMissingAsync()
    {
        var missing = _all.Where(f => f.IsMissingAtDestination).ToList();
        if (missing.Count == 0)
        {
            await Ui.ShowMessageAsync(
                "Nothing to forget", "No files are marked as missing at their destination.");
            return;
        }

        var confirmed = await Ui.ConfirmAsync(
            "Forget missing downloads",
            $"{missing.Count} file(s) were downloaded before but their copies are no longer at " +
            "the destination.\n\nForgetting them clears their download history, so they will be " +
            "downloaded again next time. Continue?",
            "Forget");

        if (!confirmed)
            return;

        _suppressRecount = true;
        try
        {
            foreach (var file in missing)
            {
                _history.Forget(file.File.Identity);
                file.ApplyHistory(HistoryLookup.NotFound);
                file.IsChecked = true;
            }
        }
        finally
        {
            _suppressRecount = false;
        }

        RecountStates();
        RecountChecked();
        RebuildVisible();
        StatusText = $"{missing.Count} missing download(s) forgotten";
    }

    /// <summary>Show only the files whose downloaded copies have disappeared.</summary>
    [RelayCommand]
    private void ShowMissingOnly() => Filter = ThumbnailFilter.MissingAtDestination;

    [RelayCommand]
    private void RevealDownload(ThumbnailViewModel? file)
    {
        if (file?.HistoryRecord is { } record)
            Ui.RevealInFileManager(record.DownloadPath);
    }

    [RelayCommand]
    private void RevealFile(ThumbnailViewModel? file)
    {
        if (file is not null)
            Ui.RevealInFileManager(file.File.FullPath);
    }

    /// <summary>
    /// Re-check the destination for every previously downloaded file. Useful after plugging
    /// an archive drive back in, or after tidying up a photo library.
    /// </summary>
    [RelayCommand]
    private async Task RecheckDestinationsAsync()
    {
        IsProgressIndeterminate = true;
        StatusText = "Checking destinations…";
        _history.InvalidateVolumeCache();

        var lookups = await Task.Run(() => _all
            .Select(f => (File: f, Lookup: _history.Lookup(f.File.Identity)))
            .ToList());

        foreach (var (file, lookup) in lookups)
            file.ApplyHistory(lookup);

        RecountStates();
        RebuildVisible();
        IsProgressIndeterminate = false;

        StatusText = MissingCount > 0
            ? $"{MissingCount:N0} downloaded file(s) are missing at their destination"
            : "All previously downloaded files are present at their destinations";
    }

    // ---- Job codes -----------------------------------------------------------------------

    [RelayCommand]
    private async Task AddJobCodeAsync()
    {
        var code = await Ui.PromptAsync(
            "New Job Code", "Job Codes label a batch of files and can be used in folder and file names.");

        if (string.IsNullOrWhiteSpace(code))
            return;

        code = code.Trim();
        if (!JobCodes.Contains(code))
        {
            JobCodes.Add(code);
            Settings.JobCodes.Add(code);
        }

        SelectedJobCode = code;
        _settingsService.Save();
    }

    [RelayCommand]
    private void RemoveJobCode(string? code)
    {
        if (code is null)
            return;

        JobCodes.Remove(code);
        Settings.JobCodes.Remove(code);

        if (SelectedJobCode == code)
            SelectedJobCode = null;

        _settingsService.Save();
    }

    // ---- Destination and backup pickers ---------------------------------------------------

    [RelayCommand]
    private async Task ChooseDestinationAsync(DestinationPanelViewModel? panel)
    {
        if (panel is null)
            return;

        var path = await Ui.PickFolderAsync(
            $"Choose where to download {panel.Title.ToLowerInvariant()}", panel.FolderPath);

        if (path is not null)
            panel.FolderPath = path;
    }

    [RelayCommand]
    private async Task EditSubfolderPresetsAsync(DestinationPanelViewModel? panel)
    {
        if (panel is null)
            return;

        _dialogDepth++;
        bool saved;
        try
        {
            saved = await Ui.ShowPresetEditorAsync(panel.Kind, subfolders: true);
        }
        finally
        {
            _dialogDepth--;
        }

        if (saved)
            panel.ReloadPresets();
    }

    [RelayCommand]
    private async Task EditRenamePresetsAsync(DestinationPanelViewModel? panel)
    {
        if (panel is null)
            return;

        _dialogDepth++;
        bool saved;
        try
        {
            saved = await Ui.ShowPresetEditorAsync(panel.Kind, subfolders: false);
        }
        finally
        {
            _dialogDepth--;
        }

        if (saved)
            panel.ReloadPresets();
    }

    [RelayCommand]
    private async Task ChooseBackupAsync(string? which)
    {
        var isVideo = which == "video";
        var path = await Ui.PickFolderAsync(
            isVideo ? "Choose the video backup folder" : "Choose the photo backup folder");

        if (path is null)
            return;

        if (isVideo)
            VideoBackupPath = path;
        else
            PhotoBackupPath = path;
    }

    [RelayCommand]
    private async Task ShowPreferencesAsync()
    {
        _dialogDepth++;
        try
        {
            await Ui.ShowPreferencesAsync();
        }
        finally
        {
            _dialogDepth--;
        }

        PhotoPanel.ReloadPresets();
        VideoPanel.ReloadPresets();
        RecountChecked();
    }

    [RelayCommand]
    private async Task ShowHistoryAsync()
    {
        _dialogDepth++;
        try
        {
            await Ui.ShowHistoryAsync();
        }
        finally
        {
            _dialogDepth--;
        }

        await RecheckDestinationsAsync();
    }

    // ---- Downloading ---------------------------------------------------------------------

    private bool CanDownload => !IsDownloading && CheckedCount > 0;

    [RelayCommand(CanExecute = nameof(CanDownload))]
    private async Task DownloadAsync()
    {
        var files = _all.Where(f => f.IsChecked).ToList();
        if (files.Count == 0)
            return;

        var needsPhotos = files.Any(f => f.Kind == FileKind.Photo);
        var needsVideos = files.Any(f => f.Kind == FileKind.Video);

        if (needsPhotos && !PhotoPanel.IsFolderSet)
        {
            await Ui.ShowMessageAsync(
                "Photo destination missing",
                "Choose a folder to download photos to before starting.");
            return;
        }

        if (needsVideos && !VideoPanel.IsFolderSet)
        {
            await Ui.ShowMessageAsync(
                "Video destination missing",
                "Choose a folder to download videos to before starting.");
            return;
        }

        if (needsPhotos && !PhotoPanel.HasEnoughSpace || needsVideos && !VideoPanel.HasEnoughSpace)
        {
            await Ui.ShowMessageAsync(
                "Not enough space",
                "The destination volume does not have room for the selected files.");
            return;
        }

        if (TransferMode == TransferMode.Move)
        {
            var confirmed = await Ui.ConfirmAsync(
                "Move files",
                $"{files.Count} file(s) will be copied to the destination and then deleted from " +
                "the source. Continue?",
                "Move");

            if (!confirmed)
                return;
        }

        var request = new DownloadRequest
        {
            Files = files.Select(f => f.File).ToList(),
            PhotoDestination = PhotoPanel.FolderPath,
            VideoDestination = VideoPanel.FolderPath,
            PhotoSubfolderPreset = PhotoPanel.SelectedSubfolderPreset,
            VideoSubfolderPreset = VideoPanel.SelectedSubfolderPreset,
            PhotoRenamePreset = PhotoPanel.SelectedRenamePreset,
            VideoRenamePreset = VideoPanel.SelectedRenamePreset,
            JobCode = SelectedJobCode,
            Mode = TransferMode,
            Conflict = Settings.ConflictResolution,
            Verify = Settings.VerifyCopies,
            BackupEnabled = BackupEnabled,
            PhotoBackupPath = PhotoBackupPath,
            VideoBackupPath = VideoBackupPath,
        };

        var byPath = files.ToDictionary(f => f.File.FullPath, StringComparer.OrdinalIgnoreCase);

        _downloadCancellation = new CancellationTokenSource();
        var token = _downloadCancellation.Token;

        IsDownloading = true;
        Progress = 0;
        ProgressText = "Starting…";

        foreach (var file in files)
            file.State = DownloadState.Downloading;

        var progress = new Progress<DownloadProgress>(p =>
        {
            Progress = p.BytesTotal > 0 ? p.BytesDone * 100.0 / p.BytesTotal : 0;
            ProgressText =
                $"{p.Completed:N0} of {p.Total:N0}  ·  " +
                $"{ThumbnailViewModel.FormatBytes(p.BytesDone)} of " +
                $"{ThumbnailViewModel.FormatBytes(p.BytesTotal)}  ·  {p.CurrentFile}";
        });

        void OnFileDone(DownloadFileResult result)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (byPath.TryGetValue(result.File.FullPath, out var vm))
                {
                    vm.ApplyDownloadResult(new DownloadFileResultLike(
                        result.Success, result.DestinationPath, result.Error));
                }
            });
        }

        try
        {
            var summary = await Task.Run(
                () => _engine.Run(request, progress, OnFileDone, token), token);

            StatusText =
                $"Downloaded {summary.Succeeded:N0} file(s)" +
                (summary.Skipped > 0 ? $", skipped {summary.Skipped:N0}" : string.Empty) +
                (summary.Failed > 0 ? $", {summary.Failed:N0} failed" : string.Empty) +
                $"  ·  {ThumbnailViewModel.FormatBytes(summary.BytesCopied)} in " +
                $"{summary.Elapsed.TotalSeconds:0.#}s";

            if (Settings.EjectAfterDownload)
                StatusText += "  ·  devices can now be removed";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download cancelled";

            foreach (var file in files.Where(f => f.State == DownloadState.Downloading))
                file.ApplyHistory(_history.Lookup(file.File.Identity));
        }
        catch (Exception ex)
        {
            StatusText = "Download failed: " + ex.Message;
            await Ui.ShowMessageAsync("Download failed", ex.Message);
        }
        finally
        {
            IsDownloading = false;
            Progress = 0;
            ProgressText = null;
            _downloadCancellation?.Dispose();
            _downloadCancellation = null;

            RecountStates();
            RecountChecked();
            RebuildVisible();

            Settings.LastJobCode = SelectedJobCode;
            _settingsService.Save();
        }
    }

    private bool CanCancelDownload => IsDownloading;

    [RelayCommand(CanExecute = nameof(CanCancelDownload))]
    private void CancelDownload() => _downloadCancellation?.Cancel();

    // ---- Property reactions ---------------------------------------------------------------

    partial void OnFilterChanged(ThumbnailFilter value)
    {
        if (_initializing)
            return;

        Settings.Filter = value;
        _settingsService.SaveThrottled();
        RebuildVisible();
    }

    partial void OnSortChanged(ThumbnailSort value)
    {
        if (_initializing)
            return;

        Settings.Sort = value;
        _settingsService.SaveThrottled();
        RebuildVisible();
    }

    partial void OnSortDirectionChanged(SortDirection value)
    {
        if (_initializing)
            return;

        Settings.SortDirection = value;
        _settingsService.SaveThrottled();
        RebuildVisible();
    }

    partial void OnThumbnailSizeChanged(int value)
    {
        if (_initializing)
            return;

        Settings.ThumbnailSize = value;
        _settingsService.SaveThrottled();
    }

    partial void OnProximityMinutesChanged(int value)
    {
        if (_initializing)
            return;

        Settings.ProximityMinutes = value;
        _settingsService.SaveThrottled();
        OnPropertyChanged(nameof(ProximityLabel));
        RebuildTimeline();
        RebuildVisible();
    }

    partial void OnTransferModeChanged(TransferMode value)
    {
        if (_initializing)
            return;

        Settings.TransferMode = value;
        _settingsService.Save();
    }

    partial void OnBackupEnabledChanged(bool value)
    {
        if (_initializing)
            return;

        Settings.BackupEnabled = value;
        _settingsService.Save();
    }

    partial void OnPhotoBackupPathChanged(string value)
    {
        if (_initializing)
            return;

        Settings.PhotoBackupPath = value;
        _settingsService.Save();
    }

    partial void OnVideoBackupPathChanged(string value)
    {
        if (_initializing)
            return;

        Settings.VideoBackupPath = value;
        _settingsService.Save();
    }

    partial void OnSelectedJobCodeChanged(string? value)
    {
        if (_initializing)
            return;

        Settings.LastJobCode = value;
        _settingsService.SaveThrottled();
        RecountChecked();
    }

    [RelayCommand]
    private void SortAscending() => SortDirection = SortDirection.Ascending;

    [RelayCommand]
    private void SortDescending() => SortDirection = SortDirection.Descending;

    [RelayCommand]
    private void ToggleSortDirection() =>
        SortDirection = SortDirection == SortDirection.Ascending
            ? SortDirection.Descending
            : SortDirection.Ascending;

    [RelayCommand]
    private static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return;

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // No browser configured; nothing useful to do.
        }
    }
}
