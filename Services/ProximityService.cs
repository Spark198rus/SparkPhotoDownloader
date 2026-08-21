using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.Services;

/// <summary>One run of shooting: files whose capture times sit close together.</summary>
public sealed class ProximityGroup
{
    public required DateTime Start { get; init; }
    public required DateTime End { get; init; }
    public required IReadOnlyList<string> FilePaths { get; init; }
    public int PhotoCount { get; init; }
    public int VideoCount { get; init; }

    public int Count => PhotoCount + VideoCount;

    /// <summary>"14 March 2026" — the header shown above a group.</summary>
    public string DateLabel => Start.Date == End.Date
        ? Start.ToString("d MMMM yyyy")
        : $"{Start:d MMM} – {End:d MMM yyyy}";

    /// <summary>"09:14 – 11:52" — the span the group covers.</summary>
    public string TimeLabel => Start.Date == End.Date && Start.TimeOfDay != End.TimeOfDay
        ? $"{Start:HH:mm} – {End:HH:mm}"
        : Start.ToString("HH:mm");
}

/// <summary>
/// Builds the Timeline shown under the sources: files are split into groups wherever the gap
/// between consecutive shots exceeds a threshold the user sets, from 5 minutes to 24 hours.
/// Selecting groups filters the thumbnail grid.
/// </summary>
public static class ProximityService
{
    /// <summary>The stops on the Timeline slider, in minutes.</summary>
    public static readonly int[] Thresholds =
        [5, 10, 15, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720, 1440];

    public static string DescribeThreshold(int minutes) => minutes switch
    {
        < 60 => $"{minutes} minutes",
        60 => "1 hour",
        1440 => "24 hours",
        _ when minutes % 60 == 0 => $"{minutes / 60} hours",
        _ => $"{minutes / 60}h {minutes % 60}m",
    };

    /// <summary>
    /// Group files by shooting proximity. <paramref name="dated"/> supplies each file's
    /// effective capture time, which is its Exif date when it has one and its modification
    /// time otherwise.
    /// </summary>
    public static IReadOnlyList<ProximityGroup> Group(
        IEnumerable<(SourceFile File, DateTime When)> dated,
        int thresholdMinutes)
    {
        var ordered = dated.OrderBy(d => d.When).ToList();
        if (ordered.Count == 0)
            return [];

        var threshold = TimeSpan.FromMinutes(Math.Max(1, thresholdMinutes));
        var groups = new List<ProximityGroup>();

        var bucket = new List<(SourceFile File, DateTime When)> { ordered[0] };

        for (var i = 1; i < ordered.Count; i++)
        {
            var previous = ordered[i - 1].When;
            var current = ordered[i].When;

            // A new calendar day always starts a new group, so days never run together.
            var newDay = current.Date != previous.Date;

            if (current - previous > threshold || newDay)
            {
                groups.Add(Build(bucket));
                bucket = [];
            }

            bucket.Add(ordered[i]);
        }

        if (bucket.Count > 0)
            groups.Add(Build(bucket));

        groups.Reverse(); // newest first, matching the thumbnail grid's default order
        return groups;
    }

    private static ProximityGroup Build(List<(SourceFile File, DateTime When)> bucket) => new()
    {
        Start = bucket[0].When,
        End = bucket[^1].When,
        FilePaths = bucket.Select(b => b.File.FullPath).ToList(),
        PhotoCount = bucket.Count(b => b.File.Kind == FileKind.Photo),
        VideoCount = bucket.Count(b => b.File.Kind == FileKind.Video),
    };
}
