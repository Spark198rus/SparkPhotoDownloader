using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.ViewModels;

/// <summary>
/// One entry in the Devices or This Computer panel: a card showing what the volume is, how
/// full it is, and how many files were found on it.
/// </summary>
public sealed partial class SourceViewModel : ViewModelBase
{
    private readonly Action<SourceViewModel>? _onCheckedChanged;

    public SourceViewModel(DownloadSource source, Action<SourceViewModel>? onCheckedChanged = null)
    {
        Source = source;
        _onCheckedChanged = onCheckedChanged;
    }

    /// <summary>The owning view model, so a card can reach the commands in its context menu.</summary>
    public MainViewModel? Owner { get; set; }

    public DownloadSource Source { get; }

    public string Id => Source.Id;

    public string Name => Source.Name;

    public string RootPath => Source.RootPath;

    public bool IsRemovable => Source.IsRemovable;

    /// <summary>True for folders the user added by hand, which can be removed again.</summary>
    public bool IsUserAdded => Source.Kind == DeviceKind.LocalFolder;

    public string KindLabel => Source.Kind switch
    {
        DeviceKind.Camera => "Camera",
        DeviceKind.MemoryCard => Source.HasCameraFolder ? "Memory card" : "Removable drive",
        DeviceKind.Phone => "Phone",
        DeviceKind.ExternalDrive => "Drive",
        DeviceKind.LocalFolder => "Folder",
        DeviceKind.Backup => "Backup",
        _ => "Source",
    };

    /// <summary>"Memory card · exFAT · DCIM" — the grey line under the device name.</summary>
    public string Subtitle
    {
        get
        {
            var parts = new List<string> { KindLabel };

            if (!string.IsNullOrWhiteSpace(Source.Description) && !IsUserAdded)
                parts.Add(Source.Description!);

            if (Source.HasCameraFolder)
                parts.Add("DCIM");

            return string.Join("  ·  ", parts);
        }
    }

    public bool HasCapacity => Source.TotalBytes > 0;

    public double UsedPercent => Source.UsedFraction * 100;

    public string CapacityText => HasCapacity
        ? $"{ThumbnailViewModel.FormatBytes(Source.UsedBytes)} of " +
          $"{ThumbnailViewModel.FormatBytes(Source.TotalBytes)} used"
        : RootPath;

    public IBrush AccentBrush => StateVisuals.KindBrush(
        Source.Kind == DeviceKind.LocalFolder ? FileKind.Unknown : FileKind.Photo);

    // ---- Mutable state -------------------------------------------------------------------

    /// <summary>Checking a source checks every file found on it.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; } = true;

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty]
    public partial int FileCount { get; set; }

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty]
    public partial int PhotoCount { get; set; }

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty]
    public partial int VideoCount { get; set; }

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [ObservableProperty]
    public partial string? ScanDetail { get; set; }

    public string StatusText
    {
        get
        {
            if (IsScanning)
                return ScanDetail is { Length: > 0 } detail
                    ? $"Scanning… {FileCount} found  ·  {detail}"
                    : $"Scanning… {FileCount} found";

            if (FileCount == 0)
                return "No photos or videos";

            var parts = new List<string>();
            if (PhotoCount > 0)
                parts.Add($"{PhotoCount} photo{(PhotoCount == 1 ? "" : "s")}");
            if (VideoCount > 0)
                parts.Add($"{VideoCount} video{(VideoCount == 1 ? "" : "s")}");

            return string.Join("  ·  ", parts);
        }
    }

    partial void OnIsCheckedChanged(bool value) => _onCheckedChanged?.Invoke(this);

    public void ResetCounts()
    {
        FileCount = 0;
        PhotoCount = 0;
        VideoCount = 0;
        ScanDetail = null;
    }
}
