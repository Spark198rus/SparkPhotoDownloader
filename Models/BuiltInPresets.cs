namespace SparkPhotoDownloader.Models;

/// <summary>
/// Date format keys shown in the preset editor, mapped to .NET custom format strings.
/// The keys mirror Rapid Photo Downloader's vocabulary so the presets read the same way.
/// </summary>
public static class DateFormats
{
    public static readonly IReadOnlyDictionary<string, string> All = new Dictionary<string, string>
    {
        ["YYYYMMDD"] = "yyyyMMdd",
        ["YYYY-MM-DD"] = "yyyy-MM-dd",
        ["YYYY_MM_DD"] = "yyyy_MM_dd",
        ["YYMMDD"] = "yyMMdd",
        ["YY-MM-DD"] = "yy-MM-dd",
        ["YY_MM_DD"] = "yy_MM_dd",
        ["YYYY"] = "yyyy",
        ["YY"] = "yy",
        ["YYYYMM"] = "yyyyMM",
        ["YYYY-MM"] = "yyyy-MM",
        ["YYYY_MM"] = "yyyy_MM",
        ["MM"] = "MM",
        ["DD"] = "dd",
        ["Month (full)"] = "MMMM",
        ["Month (abbreviated)"] = "MMM",
        ["Weekday (full)"] = "dddd",
        ["Weekday (abbreviated)"] = "ddd",
        ["HHMMSS"] = "HHmmss",
        ["HHMM"] = "HHmm",
        ["HH-MM-SS"] = "HH-mm-ss",
        ["HH-MM"] = "HH-mm",
        ["HH"] = "HH",
        ["Minutes"] = "mm",
        ["Seconds"] = "ss",
    };

    public const string Default = "YYYYMMDD";

    public static string Resolve(string? key) =>
        key is not null && All.TryGetValue(key, out var fmt) ? fmt : All[Default];
}

/// <summary>
/// The presets the program starts with. They reproduce Rapid Photo Downloader's built-in
/// subfolder and rename schemes.
/// </summary>
public static class BuiltInPresets
{
    private static NamePreset Make(string name, params NamePart[] parts) =>
        new() { Name = name, IsBuiltIn = true, Parts = [.. parts] };

    public static List<NamePreset> PhotoSubfolders() =>
    [
        Make("Date (YYYY/YYYYMMDD)",
            NamePart.Date("YYYY"), NamePart.Sep(), NamePart.Date("YYYYMMDD")),
        Make("Date (YYYY/YYYY-MM-DD)",
            NamePart.Date("YYYY"), NamePart.Sep(), NamePart.Date("YYYY-MM-DD")),
        Make("Date (YYYY/YYYY_MM_DD)",
            NamePart.Date("YYYY"), NamePart.Sep(), NamePart.Date("YYYY_MM_DD")),
        Make("Date and Job Code (YYYY/YYYYMM_Job Code)",
            NamePart.Date("YYYY"), NamePart.Sep(), NamePart.Date("YYYYMM"),
            NamePart.Literal("_"), NamePart.Of(NameToken.JobCode)),
        Make("Date and Job Code Subfolder (YYYY/YYYYMM/Job Code)",
            NamePart.Date("YYYY"), NamePart.Sep(), NamePart.Date("YYYYMM"),
            NamePart.Sep(), NamePart.Of(NameToken.JobCode)),
        Make("Date and Camera (YYYY/YYYY-MM-DD/Camera)",
            NamePart.Date("YYYY"), NamePart.Sep(), NamePart.Date("YYYY-MM-DD"),
            NamePart.Sep(), NamePart.Of(NameToken.ShortCameraModel)),
        Make("Flat (no subfolder)"),
    ];

    public static List<NamePreset> VideoSubfolders() => PhotoSubfolders();

    public static List<NamePreset> PhotoRenames() =>
    [
        Make("Original Filename", NamePart.Of(NameToken.OriginalName)),
        Make("Date-Time and Downloads today",
            NamePart.Date("YYYYMMDD"), NamePart.Literal("-"), NamePart.Date("HHMM"),
            NamePart.Literal("-"), NamePart.Of(NameToken.DownloadsToday, 2)),
        Make("Date and Downloads today",
            NamePart.Date("YYYYMMDD"), NamePart.Literal("-"),
            NamePart.Of(NameToken.DownloadsToday, 2)),
        Make("Date-Time and Image number",
            NamePart.Date("YYYYMMDD"), NamePart.Literal("-"),
            NamePart.Of(NameToken.ImageNumber, 4)),
        Make("Date-Time and Job Code",
            NamePart.Date("YYYYMMDD"), NamePart.Literal("-"), NamePart.Date("HHMM"),
            NamePart.Literal("-"), NamePart.Of(NameToken.JobCode)),
        Make("Date and Job Code",
            NamePart.Date("YYYYMMDD"), NamePart.Literal("-"), NamePart.Of(NameToken.JobCode)),
        Make("Date-Time and Original Filename",
            NamePart.Date("YYYYMMDD"), NamePart.Literal("-"), NamePart.Date("HHMMSS"),
            NamePart.Literal("-"), NamePart.Of(NameToken.OriginalName)),
        Make("Camera and Date-Time",
            NamePart.Of(NameToken.ShortCameraModel), NamePart.Literal("-"),
            NamePart.Date("YYYYMMDD"), NamePart.Literal("-"), NamePart.Date("HHMMSS")),
    ];

    public static List<NamePreset> VideoRenames() => PhotoRenames();
}
