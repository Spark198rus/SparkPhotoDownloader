using System.Globalization;
using System.Text;
using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.Services;

/// <summary>
/// The counters a download run advances as it names files. Held separately from the
/// generator so a preview can be produced without disturbing the real values.
/// </summary>
public sealed class SequenceState
{
    /// <summary>Files downloaded today, across sessions. Resets at midnight.</summary>
    public int DownloadsToday { get; set; }

    /// <summary>Files downloaded since the program started.</summary>
    public int SessionSequence { get; set; } = 1;

    /// <summary>A counter the user controls, which never resets on its own.</summary>
    public int StoredSequence { get; set; } = 1;

    /// <summary>Position within the subfolder the current file is being written to.</summary>
    public int SubfolderSequence { get; set; } = 1;

    /// <summary>Index behind the a, b, ... z, aa, ab ... sequence letter.</summary>
    public int LetterIndex { get; set; }

    public SequenceState Clone() => new()
    {
        DownloadsToday = DownloadsToday,
        SessionSequence = SessionSequence,
        StoredSequence = StoredSequence,
        SubfolderSequence = SubfolderSequence,
        LetterIndex = LetterIndex,
    };
}

/// <summary>Everything a single name generation needs to know.</summary>
public sealed class NameContext
{
    public required SourceFile File { get; init; }
    public required FileMetadata Metadata { get; init; }
    public required SequenceState Sequences { get; init; }
    public string? JobCode { get; init; }
    public DateTime DownloadTime { get; init; } = DateTime.Now;
}

/// <summary>
/// Turns a <see cref="NamePreset"/> plus a file into a destination subfolder path and a file
/// name, the way Rapid Photo Downloader's rename and subfolder generation work.
/// </summary>
public static class NameGenerator
{
    /// <summary>
    /// Build the relative subfolder path for a file. Returns an empty string for a preset
    /// with no parts, meaning "put it straight in the destination folder".
    /// </summary>
    public static string GenerateSubfolder(NamePreset? preset, NameContext context)
    {
        if (preset is null || preset.Parts.Count == 0)
            return string.Empty;

        var segments = new List<string>();
        var current = new StringBuilder();

        foreach (var part in preset.Parts)
        {
            if (part.Token == NameToken.Separator)
            {
                segments.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(Render(part, context));
        }

        segments.Add(current.ToString());

        var cleaned = segments
            .Select(SanitizeSegment)
            .Where(s => s.Length > 0)
            .ToArray();

        return cleaned.Length == 0 ? string.Empty : Path.Combine(cleaned);
    }

    /// <summary>
    /// Build the file name, always keeping the source file's extension. A preset that
    /// produces nothing falls back to the original name so a file can never end up nameless.
    /// </summary>
    public static string GenerateFileName(NamePreset? preset, NameContext context)
    {
        var extension = Path.GetExtension(context.File.FileName);

        if (preset is null || preset.Parts.Count == 0)
            return context.File.FileName;

        var builder = new StringBuilder();
        foreach (var part in preset.Parts)
        {
            if (part.Token == NameToken.Separator)
                continue;
            builder.Append(Render(part, context));
        }

        var name = SanitizeSegment(builder.ToString());
        if (name.Length == 0)
            name = Path.GetFileNameWithoutExtension(context.File.FileName);

        return name + extension;
    }

    // -------------------------------------------------------------------------------------
    // Token rendering
    // -------------------------------------------------------------------------------------

    private static string Render(NamePart part, NameContext context)
    {
        var value = RenderRaw(part, context);

        return part.Case switch
        {
            TextCase.Upper => value.ToUpperInvariant(),
            TextCase.Lower => value.ToLowerInvariant(),
            _ => value,
        };
    }

    private static string RenderRaw(NamePart part, NameContext context)
    {
        var file = context.File;
        var meta = context.Metadata;
        var seq = context.Sequences;

        return part.Token switch
        {
            NameToken.Text => part.Text ?? string.Empty,

            NameToken.Date => ResolveDate(part.DateSource, context)
                .ToString(DateFormats.Resolve(part.Format), CultureInfo.CurrentCulture),

            NameToken.OriginalName => Path.GetFileNameWithoutExtension(file.FileName),

            NameToken.Extension => file.Extension,

            NameToken.ImageNumber => ExtractImageNumber(file.FileName, part.Digits),

            NameToken.Aperture => meta.Aperture is { } f
                ? f.ToString("0.#", CultureInfo.InvariantCulture)
                : string.Empty,

            NameToken.Iso => meta.Iso?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,

            NameToken.ExposureTime => meta.ExposureTime ?? string.Empty,

            NameToken.FocalLength => meta.FocalLength is { } fl
                ? fl.ToString("0.#", CultureInfo.InvariantCulture) + "mm"
                : string.Empty,

            NameToken.CameraMake => meta.CameraMake ?? string.Empty,
            NameToken.CameraModel => meta.CameraModel ?? string.Empty,
            NameToken.ShortCameraModel => meta.ShortCameraModel ?? string.Empty,
            NameToken.SerialNumber => meta.SerialNumber ?? string.Empty,
            NameToken.LensModel => meta.LensModel ?? string.Empty,

            NameToken.ShutterCount => meta.ShutterCount?.ToString(CultureInfo.InvariantCulture)
                ?? string.Empty,

            NameToken.Width => meta.Width?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            NameToken.Height => meta.Height?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            NameToken.Codec => meta.Codec ?? string.Empty,

            NameToken.FrameRate => meta.FrameRate is { } fps
                ? fps.ToString("0.##", CultureInfo.InvariantCulture)
                : string.Empty,

            NameToken.Duration => meta.Duration is { } d
                ? ((int)d.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s"
                : string.Empty,

            NameToken.DownloadsToday => Pad(seq.DownloadsToday, part.Digits),
            NameToken.SessionSequence => Pad(seq.SessionSequence, part.Digits),
            NameToken.StoredSequence => Pad(seq.StoredSequence, part.Digits),
            NameToken.SubfolderSequence => Pad(seq.SubfolderSequence, part.Digits),
            NameToken.SequenceLetter => SequenceLetter(seq.LetterIndex),

            NameToken.JobCode => context.JobCode ?? string.Empty,
            NameToken.DeviceName => file.SourceName,

            _ => string.Empty,
        };
    }

    private static DateTime ResolveDate(DateSource source, NameContext context) => source switch
    {
        DateSource.CaptureDate => context.Metadata.CaptureDate
            ?? context.File.ModifiedUtc.ToLocalTime(),
        DateSource.ModificationDate => context.File.ModifiedUtc.ToLocalTime(),
        DateSource.Today => DateTime.Today,
        DateSource.Yesterday => DateTime.Today.AddDays(-1),
        DateSource.DownloadTime => context.DownloadTime,
        _ => context.File.ModifiedUtc.ToLocalTime(),
    };

    private static string Pad(int value, int digits) =>
        value.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Clamp(digits, 1, 8), '0');

    /// <summary>
    /// The digits a camera puts in a file name, e.g. IMG_1234 -> 1234. When
    /// <paramref name="digits"/> is smaller than the number found, the last digits are used.
    /// </summary>
    private static string ExtractImageNumber(string fileName, int digits)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var end = stem.Length;

        while (end > 0 && !char.IsDigit(stem[end - 1]))
            end--;

        var start = end;
        while (start > 0 && char.IsDigit(stem[start - 1]))
            start--;

        if (start == end)
            return string.Empty;

        var number = stem[start..end];
        digits = Math.Clamp(digits, 1, 8);

        return number.Length > digits ? number[^digits..] : number;
    }

    /// <summary>a, b, ... z, aa, ab, ... zz, aaa ...</summary>
    public static string SequenceLetter(int index)
    {
        var builder = new StringBuilder();
        index = Math.Max(0, index);

        do
        {
            builder.Insert(0, (char)('a' + index % 26));
            index = index / 26 - 1;
        }
        while (index >= 0);

        return builder.ToString();
    }

    // -------------------------------------------------------------------------------------
    // Sanitising
    // -------------------------------------------------------------------------------------

    private static readonly char[] Invalid =
        [.. Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).Distinct()];

    /// <summary>Windows refuses these as file or folder names regardless of extension.</summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Make one path segment safe: drop illegal characters, collapse runs of whitespace and
    /// separators, and refuse the reserved device names.
    /// </summary>
    public static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return string.Empty;

        var builder = new StringBuilder(segment.Length);
        foreach (var c in segment)
            builder.Append(Array.IndexOf(Invalid, c) >= 0 || char.IsControl(c) ? '_' : c);

        var cleaned = builder.ToString();

        // Collapse the underscores that dropped tokens leave behind, e.g. "2024__Job".
        while (cleaned.Contains("__", StringComparison.Ordinal))
            cleaned = cleaned.Replace("__", "_", StringComparison.Ordinal);

        cleaned = cleaned.Trim().Trim('_', '-', '.').Trim();

        if (cleaned.Length == 0)
            return string.Empty;

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(cleaned)))
            cleaned = "_" + cleaned;

        return cleaned.Length > 200 ? cleaned[..200] : cleaned;
    }

    /// <summary>
    /// A short human description of a preset, used under the preset dropdowns, e.g.
    /// "YYYY / YYYYMMDD" or "Original Filename".
    /// </summary>
    public static string Describe(NamePreset? preset)
    {
        if (preset is null || preset.Parts.Count == 0)
            return "(none)";

        var builder = new StringBuilder();
        foreach (var part in preset.Parts)
        {
            builder.Append(part.Token switch
            {
                NameToken.Separator => " / ",
                NameToken.Text => part.Text,
                NameToken.Date => part.Format,
                _ => TokenLabels.Describe(part.Token),
            });
        }

        return builder.ToString();
    }
}

/// <summary>Human labels for naming tokens, shared by the preset editor and previews.</summary>
public static class TokenLabels
{
    private static readonly Dictionary<NameToken, string> Labels = new()
    {
        [NameToken.Text] = "Text",
        [NameToken.Separator] = "Subfolder separator",
        [NameToken.Date] = "Date / time",
        [NameToken.OriginalName] = "Original filename",
        [NameToken.ImageNumber] = "Image number",
        [NameToken.Extension] = "Extension",
        [NameToken.Aperture] = "Aperture",
        [NameToken.Iso] = "ISO",
        [NameToken.ExposureTime] = "Exposure time",
        [NameToken.FocalLength] = "Focal length",
        [NameToken.CameraMake] = "Camera make",
        [NameToken.CameraModel] = "Camera model",
        [NameToken.ShortCameraModel] = "Camera model (short)",
        [NameToken.SerialNumber] = "Serial number",
        [NameToken.ShutterCount] = "Shutter count",
        [NameToken.LensModel] = "Lens",
        [NameToken.Width] = "Width",
        [NameToken.Height] = "Height",
        [NameToken.Codec] = "Codec",
        [NameToken.FrameRate] = "Frame rate",
        [NameToken.Duration] = "Duration",
        [NameToken.DownloadsToday] = "Downloads today",
        [NameToken.SessionSequence] = "Session number",
        [NameToken.StoredSequence] = "Stored number",
        [NameToken.SubfolderSequence] = "Subfolder number",
        [NameToken.SequenceLetter] = "Sequence letter",
        [NameToken.JobCode] = "Job Code",
        [NameToken.DeviceName] = "Device name",
    };

    public static string Describe(NameToken token) =>
        Labels.TryGetValue(token, out var label) ? label : token.ToString();

    public static IEnumerable<NameToken> All => Labels.Keys;
}
