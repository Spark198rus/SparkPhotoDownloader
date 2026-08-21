using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SparkPhotoDownloader.Models;
using SparkPhotoDownloader.Services;

namespace SparkPhotoDownloader.ViewModels;

/// <summary>One row of the download history window.</summary>
public sealed partial class HistoryRowViewModel : ViewModelBase
{
    public HistoryRowViewModel(DownloadRecord record, DestinationStatus status)
    {
        Record = record;
        Status = status;
    }

    public DownloadRecord Record { get; }

    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(IsMissing))]
    [ObservableProperty]
    public partial DestinationStatus Status { get; set; }

    public string FileName => Record.FileName;

    public string DownloadPath => Record.DownloadPath;

    public string SizeText => ThumbnailViewModel.FormatBytes(Record.Size);

    public string WhenText => Record.DownloadedAtLocal.ToString("d MMM yyyy HH:mm");

    public string SourceText => Record.SourceName ?? string.Empty;

    public string JobCodeText => Record.JobCode ?? string.Empty;

    public bool IsMissing => Status == DestinationStatus.Missing;

    public string StatusText => Status switch
    {
        DestinationStatus.Present => "Present",
        DestinationStatus.Missing => "Missing",
        DestinationStatus.Offline => "Drive offline",
        _ => "Unknown",
    };

    public IBrush StatusBrush => Status switch
    {
        DestinationStatus.Present => StateVisuals.Brush(DownloadState.Downloaded),
        DestinationStatus.Missing => StateVisuals.Brush(DownloadState.MissingAtDestination),
        _ => StateVisuals.Brush(DownloadState.PreviouslyDownloaded),
    };
}

/// <summary>
/// The download history window: everything the program has ever downloaded, whether each
/// copy is still there, and the ability to forget rows so files can be fetched again.
/// </summary>
public sealed partial class HistoryViewModel : ViewModelBase
{
    private readonly DownloadHistoryService _history;
    private readonly IUiServices _ui;
    private List<HistoryRowViewModel> _allRows = [];

    public HistoryViewModel(DownloadHistoryService history, IUiServices ui)
    {
        _history = history;
        _ui = ui;
        _ = LoadAsync();
    }

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string Summary { get; set; } = "Loading…";

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ShowMissingOnly { get; set; }

    [ObservableProperty]
    public partial HistoryRowViewModel? SelectedRow { get; set; }

    [RelayCommand]
    private async Task LoadAsync()
    {
        IsLoading = true;
        Summary = "Reading the history…";

        var rows = await Task.Run(() =>
        {
            _history.InvalidateVolumeCache();
            return _history.Recent()
                .Select(r => new HistoryRowViewModel(r, _history.CheckDestination(r.DownloadPath)))
                .ToList();
        });

        _allRows = rows;
        ApplyFilter();
        IsLoading = false;
    }

    private void ApplyFilter()
    {
        IEnumerable<HistoryRowViewModel> query = _allRows;

        if (ShowMissingOnly)
            query = query.Where(r => r.IsMissing);

        if (!string.IsNullOrWhiteSpace(SearchText))
        {
            var term = SearchText.Trim();
            query = query.Where(r =>
                r.FileName.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.DownloadPath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.SourceText.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.JobCodeText.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var filtered = query.ToList();

        Rows.Clear();
        foreach (var row in filtered)
            Rows.Add(row);

        var missing = _allRows.Count(r => r.IsMissing);
        Summary = $"{_allRows.Count:N0} downloads recorded" +
                  (missing > 0 ? $"  ·  {missing:N0} missing at destination" : string.Empty) +
                  (filtered.Count != _allRows.Count ? $"  ·  showing {filtered.Count:N0}" : string.Empty);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowMissingOnlyChanged(bool value) => ApplyFilter();

    [RelayCommand]
    private void Reveal(HistoryRowViewModel? row)
    {
        if (row is not null)
            _ui.RevealInFileManager(row.DownloadPath);
    }

    /// <summary>Forget one row, so that file will be offered for download again.</summary>
    [RelayCommand]
    private void Forget(HistoryRowViewModel? row)
    {
        if (row is null)
            return;

        _history.Forget(row.Record.Identity);
        _allRows.Remove(row);
        Rows.Remove(row);
        ApplyFilter();
    }

    [RelayCommand]
    private async Task ForgetMissingAsync()
    {
        var missing = _allRows.Where(r => r.IsMissing).ToList();
        if (missing.Count == 0)
        {
            await _ui.ShowMessageAsync("Nothing to forget", "Every downloaded copy is present.");
            return;
        }

        var confirmed = await _ui.ConfirmAsync(
            "Forget missing downloads",
            $"{missing.Count:N0} recorded download(s) are no longer at their destination.\n\n" +
            "Forgetting them means those files will be offered for download again next time " +
            "their source is connected. Continue?",
            "Forget");

        if (!confirmed)
            return;

        foreach (var row in missing)
        {
            _history.Forget(row.Record.Identity);
            _allRows.Remove(row);
        }

        ApplyFilter();
    }
}
