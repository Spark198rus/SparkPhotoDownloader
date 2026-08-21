namespace SparkPhotoDownloader.Models;

/// <summary>
/// Metadata read from a photo or video, used for naming, the timeline, and the details pane.
/// Every field is optional because sources vary wildly in what they record.
/// </summary>
public sealed class FileMetadata
{
    /// <summary>When the shot was taken. Falls back to file modification time when absent.</summary>
    public DateTime? CaptureDate { get; init; }

    public string? CameraMake { get; init; }
    public string? CameraModel { get; init; }
    public string? SerialNumber { get; init; }
    public string? LensModel { get; init; }
    public double? Aperture { get; init; }
    public int? Iso { get; init; }
    public string? ExposureTime { get; init; }
    public double? FocalLength { get; init; }
    public long? ShutterCount { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Codec { get; init; }
    public double? FrameRate { get; init; }
    public TimeSpan? Duration { get; init; }

    /// <summary>Exif orientation (1..8); 1 when unknown.</summary>
    public int Orientation { get; init; } = 1;

    public static readonly FileMetadata Empty = new();

    /// <summary>
    /// The camera model without the make repeated inside it, e.g. "Canon EOS 5D Mark III" -> "5D Mark III".
    /// </summary>
    public string? ShortCameraModel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CameraModel))
                return CameraModel;

            var model = CameraModel.Trim();
            if (!string.IsNullOrWhiteSpace(CameraMake))
            {
                var make = CameraMake.Trim().Split(' ')[0];
                if (model.StartsWith(make, StringComparison.OrdinalIgnoreCase))
                    model = model[make.Length..].Trim();
            }

            // Strip a leading product-line word ("EOS", "DSC", "ILCE") the way RPD does.
            var parts = model.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && parts[0].Length <= 4 && parts[0].All(char.IsLetter))
                model = string.Join(' ', parts[1..]);

            return model.Length == 0 ? CameraModel : model;
        }
    }
}
