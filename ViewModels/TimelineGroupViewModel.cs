using CommunityToolkit.Mvvm.ComponentModel;
using SparkPhotoDownloader.Services;

namespace SparkPhotoDownloader.ViewModels;

/// <summary>
/// One row of the Timeline: a cluster of shots taken close together. Selecting rows narrows
/// the thumbnail grid to those files.
/// </summary>
public sealed partial class TimelineGroupViewModel : ViewModelBase
{
    private readonly Action _onSelectionChanged;

    public TimelineGroupViewModel(ProximityGroup group, Action onSelectionChanged)
    {
        Group = group;
        _onSelectionChanged = onSelectionChanged;
        Paths = new HashSet<string>(group.FilePaths, StringComparer.OrdinalIgnoreCase);
    }

    public ProximityGroup Group { get; }

    public HashSet<string> Paths { get; }

    public string DateLabel => Group.DateLabel;

    public string TimeLabel => Group.TimeLabel;

    public int Count => Group.Count;

    public string CountText
    {
        get
        {
            var parts = new List<string>();
            if (Group.PhotoCount > 0)
                parts.Add($"{Group.PhotoCount} photo{(Group.PhotoCount == 1 ? "" : "s")}");
            if (Group.VideoCount > 0)
                parts.Add($"{Group.VideoCount} video{(Group.VideoCount == 1 ? "" : "s")}");
            return string.Join(", ", parts);
        }
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value) => _onSelectionChanged();
}
