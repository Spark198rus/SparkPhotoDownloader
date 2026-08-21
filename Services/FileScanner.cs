using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.Services;

/// <summary>Progress report emitted while a source is being walked.</summary>
/// <param name="SourceName">The device or folder currently being read.</param>
/// <param name="FilesFound">Downloadable files found so far.</param>
/// <param name="CurrentFolder">The folder being read, for the status line.</param>
public readonly record struct ScanProgress(string SourceName, int FilesFound, string CurrentFolder);

/// <summary>
/// Walks a download source and returns every photo and video on it, with sidecar files
/// attached to the photo or video they belong to.
/// </summary>
public sealed class FileScanner
{
    /// <summary>Folders that never contain anything worth downloading.</summary>
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "$RECYCLE.BIN", "System Volume Information", "RECYCLER", ".Trashes", ".Spotlight-V100",
        ".fseventsd", "THMBNL", "MISC", ".thumbnails", "found.000",
    };

    /// <summary>
    /// Scan one source. Runs on a background thread; report progress through
    /// <paramref name="progress"/> and stop early through <paramref name="cancellation"/>.
    /// </summary>
    public IReadOnlyList<SourceFile> Scan(
        DownloadSource source,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellation = default)
    {
        var found = new List<SourceFile>();
        var sidecars = new List<(string Path, string Folder, string BaseName)>();

        var root = source.RootPath;
        if (!Directory.Exists(root))
            return found;

        var lastReport = DateTime.UtcNow;

        foreach (var folder in EnumerateFolders(root, cancellation))
        {
            cancellation.ThrowIfCancellationRequested();

            string[] files;
            try
            {
                files = Directory.GetFiles(folder);
            }
            catch (Exception)
            {
                continue;
            }

            var relative = MakeRelative(root, folder);

            foreach (var path in files)
            {
                cancellation.ThrowIfCancellationRequested();

                var extension = FileTypeRegistry.NormalizeExtension(path);
                var kind = FileTypeRegistry.Classify(extension);

                if (kind == FileKind.Sidecar)
                {
                    sidecars.Add((path, folder, Path.GetFileNameWithoutExtension(path)));
                    continue;
                }

                if (!FileTypeRegistry.IsDownloadable(kind))
                    continue;

                FileInfo info;
                try
                {
                    info = new FileInfo(path);
                    if (!info.Exists || (info.Attributes & FileAttributes.System) != 0)
                        continue;
                }
                catch (Exception)
                {
                    continue;
                }

                found.Add(new SourceFile
                {
                    FullPath = path,
                    FileName = info.Name,
                    Extension = extension,
                    Size = info.Length,
                    ModifiedUtc = info.LastWriteTimeUtc,
                    Kind = kind,
                    SourceId = source.Id,
                    SourceName = source.Name,
                    RelativeFolder = relative,
                });
            }

            if (progress is not null && DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(120))
            {
                lastReport = DateTime.UtcNow;
                progress.Report(new ScanProgress(source.Name, found.Count, relative));
            }
        }

        AttachSidecars(found, sidecars);
        progress?.Report(new ScanProgress(source.Name, found.Count, string.Empty));
        return found;
    }

    /// <summary>Attach XMP/THM/WAV companions to the file they describe.</summary>
    private static void AttachSidecars(
        List<SourceFile> files,
        List<(string Path, string Folder, string BaseName)> sidecars)
    {
        if (sidecars.Count == 0)
            return;

        var byFolderAndBase = files
            .GroupBy(f => (Folder: Path.GetDirectoryName(f.FullPath) ?? string.Empty,
                           Base: Path.GetFileNameWithoutExtension(f.FileName)),
                     FolderBaseComparer.Instance)
            .ToDictionary(g => g.Key, g => g.ToList(), FolderBaseComparer.Instance);

        foreach (var (path, folder, baseName) in sidecars)
        {
            // "IMG_1234.CR2.xmp" describes IMG_1234.CR2; "IMG_1234.xmp" describes IMG_1234.*
            var keys = new[]
            {
                (folder, Path.GetFileNameWithoutExtension(baseName)),
                (folder, baseName),
            };

            foreach (var key in keys)
            {
                if (!byFolderAndBase.TryGetValue(key, out var owners))
                    continue;

                foreach (var owner in owners)
                    owner.Sidecars.Add(path);
                break;
            }
        }
    }

    /// <summary>Depth-first folder walk that quietly skips anything it cannot read.</summary>
    private static IEnumerable<string> EnumerateFolders(string root, CancellationToken cancellation)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var current = stack.Pop();
            yield return current;

            string[] children;
            try
            {
                children = Directory.GetDirectories(current);
            }
            catch (Exception)
            {
                continue;
            }

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (SkippedFolders.Contains(name))
                    continue;

                try
                {
                    if ((new DirectoryInfo(child).Attributes & FileAttributes.System) != 0)
                        continue;
                }
                catch (Exception)
                {
                    continue;
                }

                stack.Push(child);
            }
        }
    }

    private static string MakeRelative(string root, string folder)
    {
        if (string.Equals(root, folder, StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        try
        {
            return Path.GetRelativePath(root, folder);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private sealed class FolderBaseComparer : IEqualityComparer<(string Folder, string Base)>
    {
        public static readonly FolderBaseComparer Instance = new();

        public bool Equals((string Folder, string Base) x, (string Folder, string Base) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Base, y.Base, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Folder, string Base) obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Folder),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Base));
    }
}
