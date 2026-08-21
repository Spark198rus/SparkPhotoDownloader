using System.Globalization;
using Avalonia.Data.Converters;
using SparkPhotoDownloader.Models;
using SparkPhotoDownloader.Services;
using SparkPhotoDownloader.ViewModels;

namespace SparkPhotoDownloader.Converters;

/// <summary>Turns the view's enums into the wording used in the dropdowns.</summary>
public sealed class EnumLabelConverter : IValueConverter
{
    public static readonly EnumLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value switch
        {
            ThumbnailSort sort => sort switch
            {
                ThumbnailSort.ModificationTime => "Modification time",
                ThumbnailSort.CaptureTime => "Capture time",
                ThumbnailSort.Checked => "Checked state",
                ThumbnailSort.FileName => "File name",
                ThumbnailSort.Extension => "Extension",
                ThumbnailSort.FileType => "File type",
                ThumbnailSort.Device => "Device",
                ThumbnailSort.Size => "File size",
                _ => sort.ToString(),
            },

            ThumbnailFilter filter => filter switch
            {
                ThumbnailFilter.All => "All files",
                ThumbnailFilter.New => "New files only",
                ThumbnailFilter.PreviouslyDownloaded => "Previously downloaded",
                ThumbnailFilter.MissingAtDestination => "Missing at destination",
                ThumbnailFilter.PhotosOnly => "Photos only",
                ThumbnailFilter.VideosOnly => "Videos only",
                _ => filter.ToString(),
            },

            TransferMode mode => mode == TransferMode.Move
                ? "Move (delete from source)"
                : "Copy (leave source intact)",

            ConflictResolution conflict => conflict switch
            {
                ConflictResolution.AddUniqueIdentifier => "Add a unique suffix",
                ConflictResolution.SkipDownload => "Skip the file",
                ConflictResolution.Overwrite => "Overwrite what is there",
                _ => conflict.ToString(),
            },

            DateSource source => source switch
            {
                DateSource.CaptureDate => "Date the shot was taken",
                DateSource.ModificationDate => "File modification date",
                DateSource.Today => "Today",
                DateSource.Yesterday => "Yesterday",
                DateSource.DownloadTime => "Time of download",
                _ => source.ToString(),
            },

            TextCase textCase => textCase switch
            {
                TextCase.Original => "Original case",
                TextCase.Upper => "UPPERCASE",
                TextCase.Lower => "lowercase",
                _ => textCase.ToString(),
            },

            NameToken token => TokenLabels.Describe(token),

            SortDirection direction => direction == SortDirection.Ascending
                ? "Oldest first"
                : "Newest first",

            _ => value?.ToString() ?? string.Empty,
        };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats the Timeline slider's minute values as "30 minutes", "2 hours", …</summary>
public sealed class ProximityLabelConverter : IValueConverter
{
    public static readonly ProximityLabelConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int minutes ? ProximityService.DescribeThreshold(minutes) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Formats a byte count as B / KB / MB / GB.</summary>
public sealed class ByteSizeConverter : IValueConverter
{
    public static readonly ByteSizeConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is long bytes ? ThumbnailViewModel.FormatBytes(bytes) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
