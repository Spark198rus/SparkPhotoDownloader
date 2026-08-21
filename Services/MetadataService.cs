using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SparkPhotoDownloader.Models;
using MetaDirectory = MetadataExtractor.Directory;

namespace SparkPhotoDownloader.Services;

/// <summary>
/// Reads capture dates and camera settings out of photos and videos. Everything here is
/// best-effort: a file with no usable metadata still downloads, it just falls back to its
/// modification time for naming and for the timeline.
/// </summary>
public sealed class MetadataService
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FileMetadata> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Read metadata for a file, memoising the result for the session.</summary>
    public FileMetadata Read(SourceFile file)
    {
        var key = file.FullPath + "|" + file.ModifiedUtc.Ticks;

        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached))
                return cached;
        }

        var metadata = ReadUncached(file);

        lock (_gate)
            _cache[key] = metadata;

        return metadata;
    }

    /// <summary>The date used for naming and grouping: capture date, or mtime when absent.</summary>
    public DateTime EffectiveDate(SourceFile file)
    {
        var captured = Read(file).CaptureDate;
        return captured ?? file.ModifiedUtc.ToLocalTime();
    }

    private FileMetadata ReadUncached(SourceFile file)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(file.FullPath);
            return file.Kind == FileKind.Video
                ? ReadVideo(directories, file)
                : ReadPhoto(directories, file);
        }
        catch (Exception)
        {
            // Unsupported or damaged files fall back to what the file system knows.
            return FileMetadata.Empty;
        }
    }

    private static FileMetadata ReadPhoto(IReadOnlyList<MetaDirectory> directories, SourceFile file)
    {
        var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();

        return new FileMetadata
        {
            CaptureDate = ReadCaptureDate(subIfd, ifd0),
            CameraMake = Clean(ifd0?.GetDescription(ExifDirectoryBase.TagMake)),
            CameraModel = Clean(ifd0?.GetDescription(ExifDirectoryBase.TagModel)),
            SerialNumber = Clean(subIfd?.GetDescription(ExifDirectoryBase.TagBodySerialNumber)),
            LensModel = Clean(subIfd?.GetDescription(ExifDirectoryBase.TagLensModel)),
            Aperture = TryDouble(subIfd, ExifDirectoryBase.TagFNumber),
            Iso = TryInt(subIfd, ExifDirectoryBase.TagIsoEquivalent),
            ExposureTime = FormatExposure(subIfd),
            FocalLength = TryDouble(subIfd, ExifDirectoryBase.TagFocalLength),
            Width = TryInt(subIfd, ExifDirectoryBase.TagExifImageWidth)
                ?? TryInt(ifd0, ExifDirectoryBase.TagImageWidth)
                ?? ReadDimension(directories, "Image Width"),
            Height = TryInt(subIfd, ExifDirectoryBase.TagExifImageHeight)
                ?? TryInt(ifd0, ExifDirectoryBase.TagImageHeight)
                ?? ReadDimension(directories, "Image Height"),
            Orientation = TryInt(ifd0, ExifDirectoryBase.TagOrientation) ?? 1,
            ShutterCount = ReadShutterCount(directories),
        };
    }

    private static FileMetadata ReadVideo(IReadOnlyList<MetaDirectory> directories, SourceFile file)
    {
        var created = FirstDate(directories,
            "Created", "Creation Time", "Date/Time Original", "Date Created");

        return new FileMetadata
        {
            CaptureDate = created,
            CameraMake = Clean(FirstDescription(directories, "Make", "Manufacturer", "Vendor")),
            CameraModel = Clean(FirstDescription(directories, "Model", "Camera Model Name")),
            Width = ParseInt(FirstDescription(directories, "Width", "Image Width", "Video Width")),
            Height = ParseInt(FirstDescription(directories, "Height", "Image Height", "Video Height")),
            Codec = Clean(FirstDescription(directories, "Compression Type", "Codec", "Format", "Compressor Name")),
            FrameRate = ParseDouble(FirstDescription(directories, "Frame Rate", "Frames Per Second")),
            Duration = ParseDuration(FirstDescription(directories, "Duration", "Duration in Seconds")),
        };
    }

    private static DateTime? ReadCaptureDate(ExifSubIfdDirectory? subIfd, ExifIfd0Directory? ifd0)
    {
        if (subIfd is not null)
        {
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var original))
                return original;
            if (subIfd.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized))
                return digitized;
        }

        if (ifd0 is not null && ifd0.TryGetDateTime(ExifDirectoryBase.TagDateTime, out var modified))
            return modified;

        return null;
    }

    /// <summary>
    /// Shutter count lives in maker notes and is named differently by every vendor, so it is
    /// found by tag name rather than by constant.
    /// </summary>
    private static long? ReadShutterCount(IReadOnlyList<MetaDirectory> directories)
    {
        var text = FirstDescription(directories,
            "Shutter Count", "Image Count", "Total Shutter Releases", "Shutter Total");
        return long.TryParse(Digits(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;
    }

    private static int? ReadDimension(IReadOnlyList<MetaDirectory> directories, string tagName) =>
        ParseInt(FirstDescription(directories, tagName));

    // ---- Tag helpers ---------------------------------------------------------------------

    /// <summary>First tag description matching any of <paramref name="names"/>, across all directories.</summary>
    private static string? FirstDescription(IReadOnlyList<MetaDirectory> directories, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var directory in directories)
            {
                foreach (var tag in directory.Tags)
                {
                    if (!string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var description = tag.Description;
                    if (!string.IsNullOrWhiteSpace(description))
                        return description;
                }
            }
        }

        return null;
    }

    private static DateTime? FirstDate(IReadOnlyList<MetaDirectory> directories, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var directory in directories)
            {
                foreach (var tag in directory.Tags)
                {
                    if (!string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (directory.TryGetDateTime(tag.Type, out var value) && value.Year > 1970)
                        return value;

                    if (DateTime.TryParse(tag.Description, CultureInfo.InvariantCulture,
                            DateTimeStyles.None, out var parsed) && parsed.Year > 1970)
                        return parsed;
                }
            }
        }

        return null;
    }

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim().Trim('\0');
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static double? TryDouble(MetaDirectory? directory, int tag) =>
        directory is not null && directory.TryGetDouble(tag, out var value) ? value : null;

    private static int? TryInt(MetaDirectory? directory, int tag) =>
        directory is not null && directory.TryGetInt32(tag, out var value) ? value : null;

    private static string? FormatExposure(ExifSubIfdDirectory? subIfd)
    {
        if (subIfd is null)
            return null;

        if (!subIfd.TryGetDouble(ExifDirectoryBase.TagExposureTime, out var seconds) || seconds <= 0)
            return Clean(subIfd.GetDescription(ExifDirectoryBase.TagExposureTime));

        // Fast shutter speeds read better as 1/250 than as 0.004.
        return seconds < 1
            ? "1_" + Math.Round(1 / seconds).ToString("0", CultureInfo.InvariantCulture)
            : seconds.ToString("0.#", CultureInfo.InvariantCulture) + "s";
    }

    private static int? ParseInt(string? text) =>
        int.TryParse(Digits(text), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    private static double? ParseDouble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var cleaned = new string(text.TakeWhile(c => char.IsDigit(c) || c is '.' or ',').ToArray())
            .Replace(',', '.');

        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;
    }

    private static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var span))
            return span;

        var seconds = ParseDouble(text);
        return seconds is not null ? TimeSpan.FromSeconds(seconds.Value) : null;
    }

    private static string Digits(string? text) =>
        text is null ? string.Empty : new string(text.Where(char.IsDigit).ToArray());
}
