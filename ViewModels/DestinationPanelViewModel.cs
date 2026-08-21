using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SparkPhotoDownloader.Models;
using SparkPhotoDownloader.Services;

namespace SparkPhotoDownloader.ViewModels;

/// <summary>
/// A folder in the destination preview tree. Folders that already exist are drawn normally;
/// folders this download would create are drawn in italics, as Rapid Photo Downloader does.
/// </summary>
public sealed partial class FolderNodeViewModel : ViewModelBase
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    /// <summary>True when the folder does not exist yet and the download would create it.</summary>
    public required bool WillBeCreated { get; init; }

    /// <summary>How many checked files would land in this folder (not counting subfolders).</summary>
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(HasFiles))]
    [ObservableProperty]
    public partial int FileCount { get; set; }

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public bool HasFiles => FileCount > 0;

    public string CountText => FileCount > 0 ? FileCount.ToString() : string.Empty;

    public FontStyle Style => WillBeCreated ? FontStyle.Italic : FontStyle.Normal;

    public string Tooltip => WillBeCreated
        ? FullPath + "\n(will be created)"
        : FullPath;
}

/// <summary>
/// One half of the right-hand column: where photos go, or where videos go, together with the
/// subfolder scheme, the renaming scheme, a preview of both, and the space situation.
/// </summary>
public sealed partial class DestinationPanelViewModel : ViewModelBase
{
    private readonly FileKind _kind;
    private readonly SettingsService _settings;
    private readonly Action _onChanged;

    /// <summary>
    /// True while the constructor is seeding properties from settings. Property change
    /// handlers write back to settings and notify the main view model, neither of which is
    /// wanted (or safe) before construction finishes.
    /// </summary>
    private readonly bool _initializing = true;

    public DestinationPanelViewModel(FileKind kind, SettingsService settings, Action onChanged)
    {
        _kind = kind;
        _settings = settings;
        _onChanged = onChanged;

        SubfolderPresets = new ObservableCollection<NamePreset>(
            kind == FileKind.Video ? settings.VideoSubfolderPresets() : settings.PhotoSubfolderPresets());

        RenamePresets = new ObservableCollection<NamePreset>(
            kind == FileKind.Video ? settings.VideoRenamePresets() : settings.PhotoRenamePresets());

        var s = settings.Settings;
        FolderPath = (kind == FileKind.Video ? s.VideoDestination : s.PhotoDestination) ?? string.Empty;

        SelectedSubfolderPreset =
            SettingsService.Find(SubfolderPresets,
                kind == FileKind.Video ? s.VideoSubfolderPreset : s.PhotoSubfolderPreset)
            ?? SubfolderPresets.FirstOrDefault();

        SelectedRenamePreset =
            SettingsService.Find(RenamePresets,
                kind == FileKind.Video ? s.VideoRenamePreset : s.PhotoRenamePreset)
            ?? RenamePresets.FirstOrDefault();

        _initializing = false;
    }

    public FileKind Kind => _kind;

    public bool IsVideo => _kind == FileKind.Video;

    public string Title => IsVideo ? "Videos" : "Photos";

    public IBrush AccentBrush => StateVisuals.KindBrush(_kind);

    public ObservableCollection<NamePreset> SubfolderPresets { get; }

    public ObservableCollection<NamePreset> RenamePresets { get; }

    public ObservableCollection<FolderNodeViewModel> Tree { get; } = [];

    // ---- Folder --------------------------------------------------------------------------

    [NotifyPropertyChangedFor(nameof(FolderDisplay))]
    [NotifyPropertyChangedFor(nameof(IsFolderSet))]
    [ObservableProperty]
    public partial string FolderPath { get; set; } = string.Empty;

    public bool IsFolderSet => !string.IsNullOrWhiteSpace(FolderPath) && Directory.Exists(FolderPath);

    public string FolderDisplay => string.IsNullOrWhiteSpace(FolderPath)
        ? "Choose a folder…"
        : FolderPath;

    partial void OnFolderPathChanged(string value)
    {
        if (_initializing)
            return;

        if (IsVideo)
            _settings.Settings.VideoDestination = value;
        else
            _settings.Settings.PhotoDestination = value;

        _settings.Save();
        _onChanged();
    }

    // ---- Presets -------------------------------------------------------------------------

    [NotifyPropertyChangedFor(nameof(SubfolderDescription))]
    [ObservableProperty]
    public partial NamePreset? SelectedSubfolderPreset { get; set; }

    [NotifyPropertyChangedFor(nameof(RenameDescription))]
    [ObservableProperty]
    public partial NamePreset? SelectedRenamePreset { get; set; }

    public string SubfolderDescription => NameGenerator.Describe(SelectedSubfolderPreset);

    public string RenameDescription => NameGenerator.Describe(SelectedRenamePreset);

    partial void OnSelectedSubfolderPresetChanged(NamePreset? value)
    {
        if (_initializing)
            return;

        if (value is null)
            return;

        if (IsVideo)
            _settings.Settings.VideoSubfolderPreset = value.Name;
        else
            _settings.Settings.PhotoSubfolderPreset = value.Name;

        _settings.Save();
        _onChanged();
    }

    partial void OnSelectedRenamePresetChanged(NamePreset? value)
    {
        if (_initializing)
            return;

        if (value is null)
            return;

        if (IsVideo)
            _settings.Settings.VideoRenamePreset = value.Name;
        else
            _settings.Settings.PhotoRenamePreset = value.Name;

        _settings.Save();
        _onChanged();
    }

    /// <summary>Reload the preset lists after the preset editor changes them.</summary>
    public void ReloadPresets()
    {
        var subfolderName = SelectedSubfolderPreset?.Name;
        var renameName = SelectedRenamePreset?.Name;

        SubfolderPresets.Clear();
        foreach (var preset in IsVideo
                     ? _settings.VideoSubfolderPresets()
                     : _settings.PhotoSubfolderPresets())
        {
            SubfolderPresets.Add(preset);
        }

        RenamePresets.Clear();
        foreach (var preset in IsVideo
                     ? _settings.VideoRenamePresets()
                     : _settings.PhotoRenamePresets())
        {
            RenamePresets.Add(preset);
        }

        SelectedSubfolderPreset = SettingsService.Find(SubfolderPresets, subfolderName)
            ?? SubfolderPresets.FirstOrDefault();
        SelectedRenamePreset = SettingsService.Find(RenamePresets, renameName)
            ?? RenamePresets.FirstOrDefault();
    }

    // ---- Preview -------------------------------------------------------------------------

    /// <summary>An example of the generated name, e.g. "20260314-0042.CR3".</summary>
    [ObservableProperty]
    public partial string ExampleName { get; set; } = "—";

    /// <summary>An example of the generated subfolder, e.g. "2026/20260314".</summary>
    [ObservableProperty]
    public partial string ExampleFolder { get; set; } = "—";

    /// <summary>Warns when a rename scheme cannot guarantee unique names.</summary>
    [NotifyPropertyChangedFor(nameof(HasNameWarning))]
    [ObservableProperty]
    public partial string? NameWarning { get; set; }

    public bool HasNameWarning => !string.IsNullOrEmpty(NameWarning);

    // ---- Totals and space ----------------------------------------------------------------

    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [ObservableProperty]
    public partial int SelectedFileCount { get; set; }

    [NotifyPropertyChangedFor(nameof(SummaryText))]
    [NotifyPropertyChangedFor(nameof(SpaceText))]
    [NotifyPropertyChangedFor(nameof(HasEnoughSpace))]
    [ObservableProperty]
    public partial long SelectedBytes { get; set; }

    [NotifyPropertyChangedFor(nameof(SpaceText))]
    [NotifyPropertyChangedFor(nameof(HasEnoughSpace))]
    [NotifyPropertyChangedFor(nameof(UsedPercent))]
    [ObservableProperty]
    public partial long DriveFreeBytes { get; set; }

    [NotifyPropertyChangedFor(nameof(SpaceText))]
    [NotifyPropertyChangedFor(nameof(UsedPercent))]
    [ObservableProperty]
    public partial long DriveTotalBytes { get; set; }

    public bool HasEnoughSpace => DriveTotalBytes == 0 || SelectedBytes <= DriveFreeBytes;

    public double UsedPercent => DriveTotalBytes > 0
        ? Math.Clamp((DriveTotalBytes - DriveFreeBytes + SelectedBytes) * 100.0 / DriveTotalBytes, 0, 100)
        : 0;

    public string SummaryText => SelectedFileCount == 0
        ? $"No {Title.ToLowerInvariant()} selected"
        : $"{SelectedFileCount} {(SelectedFileCount == 1 ? Title[..^1] : Title).ToLowerInvariant()}" +
          $"  ·  {ThumbnailViewModel.FormatBytes(SelectedBytes)}";

    public string SpaceText
    {
        get
        {
            if (DriveTotalBytes == 0)
                return string.Empty;

            var free = ThumbnailViewModel.FormatBytes(DriveFreeBytes);
            return HasEnoughSpace
                ? $"{free} free"
                : $"Not enough space: needs {ThumbnailViewModel.FormatBytes(SelectedBytes)}, {free} free";
        }
    }

    /// <summary>Refresh the free-space figures for the chosen folder's volume.</summary>
    public void RefreshDriveSpace()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(FolderPath));
            if (string.IsNullOrEmpty(root))
                return;

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                DriveTotalBytes = 0;
                DriveFreeBytes = 0;
                return;
            }

            DriveTotalBytes = drive.TotalSize;
            DriveFreeBytes = drive.AvailableFreeSpace;
        }
        catch (Exception)
        {
            DriveTotalBytes = 0;
            DriveFreeBytes = 0;
        }
    }

    /// <summary>
    /// Rebuild the folder tree: the folders already under the destination, plus the ones this
    /// download would create, with a count of the files that would land in each.
    /// </summary>
    public void BuildTree(IReadOnlyDictionary<string, int> plannedRelativeFolders)
    {
        Tree.Clear();

        if (!IsFolderSet)
            return;

        var root = new FolderNodeViewModel
        {
            Name = Path.GetFileName(FolderPath.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n
                ? n
                : FolderPath,
            FullPath = FolderPath,
            WillBeCreated = false,
        };

        // Existing structure, two levels deep: enough to orient the user without stalling on
        // a destination that holds years of downloads.
        AddExistingChildren(root, depth: 2, maxPerLevel: 60);

        foreach (var (relative, count) in plannedRelativeFolders.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            var node = root;

            if (relative.Length > 0)
            {
                foreach (var segment in relative.Split(
                             [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    var childPath = Path.Combine(node.FullPath, segment);
                    var existing = node.Children.FirstOrDefault(
                        c => string.Equals(c.Name, segment, StringComparison.OrdinalIgnoreCase));

                    if (existing is null)
                    {
                        existing = new FolderNodeViewModel
                        {
                            Name = segment,
                            FullPath = childPath,
                            WillBeCreated = !Directory.Exists(childPath),
                        };
                        node.Children.Add(existing);
                    }

                    node = existing;
                }
            }

            node.FileCount += count;
        }

        Sort(root);
        Tree.Add(root);
    }

    private static void AddExistingChildren(FolderNodeViewModel node, int depth, int maxPerLevel)
    {
        if (depth <= 0)
            return;

        try
        {
            var children = Directory.EnumerateDirectories(node.FullPath).Take(maxPerLevel);
            foreach (var child in children)
            {
                var childNode = new FolderNodeViewModel
                {
                    Name = Path.GetFileName(child),
                    FullPath = child,
                    WillBeCreated = false,
                };

                AddExistingChildren(childNode, depth - 1, maxPerLevel);
                node.Children.Add(childNode);
            }
        }
        catch (Exception)
        {
            // An unreadable destination still shows its root.
        }
    }

    private static void Sort(FolderNodeViewModel node)
    {
        var sorted = node.Children
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        node.Children.Clear();
        foreach (var child in sorted)
        {
            Sort(child);
            node.Children.Add(child);
        }
    }
}
