using System.Security.Cryptography;
using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.Services;

/// <summary>Everything a download run needs, gathered from the right-hand panels.</summary>
public sealed class DownloadRequest
{
    public required IReadOnlyList<SourceFile> Files { get; init; }
    public required string PhotoDestination { get; init; }
    public required string VideoDestination { get; init; }
    public NamePreset? PhotoSubfolderPreset { get; init; }
    public NamePreset? VideoSubfolderPreset { get; init; }
    public NamePreset? PhotoRenamePreset { get; init; }
    public NamePreset? VideoRenamePreset { get; init; }
    public string? JobCode { get; init; }
    public TransferMode Mode { get; init; } = TransferMode.Copy;
    public ConflictResolution Conflict { get; init; } = ConflictResolution.SkipDownload;
    public bool Verify { get; init; }
    public bool BackupEnabled { get; init; }
    public string? PhotoBackupPath { get; init; }
    public string? VideoBackupPath { get; init; }
}

/// <summary>What happened to one file.</summary>
public sealed class DownloadFileResult
{
    public required SourceFile File { get; init; }
    public required bool Success { get; init; }
    public string? DestinationPath { get; init; }
    public string? Error { get; init; }

    /// <summary>True when the copy already existed and the conflict rule said to leave it.</summary>
    public bool Skipped { get; init; }
}

/// <summary>Progress for the bar above the Download button.</summary>
/// <param name="Completed">Files finished so far.</param>
/// <param name="Total">Files in the run.</param>
/// <param name="BytesDone">Bytes written so far, including the file in flight.</param>
/// <param name="BytesTotal">Bytes the whole run will write.</param>
/// <param name="CurrentFile">Name of the file being written.</param>
public readonly record struct DownloadProgress(
    int Completed, int Total, long BytesDone, long BytesTotal, string CurrentFile);

/// <summary>Totals reported once a run finishes.</summary>
public sealed record DownloadSummary(
    int Succeeded, int Failed, int Skipped, long BytesCopied, TimeSpan Elapsed)
{
    public int Total => Succeeded + Failed + Skipped;
}

/// <summary>
/// Copies (or moves) the checked files to their destinations, generating subfolders and
/// names as it goes, writing backups, and recording every success in the download history.
/// </summary>
public sealed class DownloadEngine
{
    private readonly MetadataService _metadata;
    private readonly DownloadHistoryService _history;
    private readonly SettingsService _settings;

    public DownloadEngine(
        MetadataService metadata, DownloadHistoryService history, SettingsService settings)
    {
        _metadata = metadata;
        _history = history;
        _settings = settings;
    }

    /// <summary>
    /// Work out where a file would land, without writing anything. Used by the destination
    /// panels to show the folder tree that a download would create.
    /// </summary>
    public string PlanRelativeFolder(SourceFile file, NamePreset? subfolderPreset, string? jobCode)
    {
        var context = new NameContext
        {
            File = file,
            Metadata = _metadata.Read(file),
            Sequences = new SequenceState(),
            JobCode = jobCode,
        };

        return NameGenerator.GenerateSubfolder(subfolderPreset, context);
    }

    /// <summary>Work out the file name a file would be given, without writing anything.</summary>
    public string PlanFileName(SourceFile file, NamePreset? renamePreset, string? jobCode)
    {
        var settings = _settings.Settings;
        var context = new NameContext
        {
            File = file,
            Metadata = _metadata.Read(file),
            JobCode = jobCode,
            Sequences = new SequenceState
            {
                DownloadsToday = settings.DownloadsToday + 1,
                StoredSequence = settings.StoredSequenceNumber,
            },
        };

        return NameGenerator.GenerateFileName(renamePreset, context);
    }

    /// <summary>
    /// Run the download. Blocking; call it from a background thread. Each file's outcome is
    /// reported through <paramref name="onFileComplete"/> as it happens so thumbnails can
    /// update live.
    /// </summary>
    public DownloadSummary Run(
        DownloadRequest request,
        IProgress<DownloadProgress>? progress = null,
        Action<DownloadFileResult>? onFileComplete = null,
        CancellationToken cancellation = default)
    {
        var started = DateTime.UtcNow;
        var settings = _settings.Settings;

        // Oldest first, so that "downloads today" and sequence letters advance in shooting
        // order rather than in whatever order the file system handed the files over.
        var files = request.Files
            .OrderBy(f => _metadata.EffectiveDate(f))
            .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sequences = new SequenceState
        {
            DownloadsToday = settings.DownloadsToday,
            StoredSequence = settings.StoredSequenceNumber,
            SessionSequence = 1,
        };

        var subfolderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var records = new List<DownloadRecord>(files.Count);

        var bytesTotal = files.Sum(f => f.Size);
        long bytesDone = 0;
        int succeeded = 0, failed = 0, skipped = 0;

        for (var index = 0; index < files.Count; index++)
        {
            cancellation.ThrowIfCancellationRequested();

            var file = files[index];
            progress?.Report(new DownloadProgress(index, files.Count, bytesDone, bytesTotal, file.FileName));

            var result = DownloadOne(file, request, sequences, subfolderCounts, records, cancellation);

            bytesDone += file.Size;

            if (result.Success)
            {
                if (result.Skipped)
                    skipped++;
                else
                    succeeded++;

                sequences.DownloadsToday++;
                sequences.SessionSequence++;
                sequences.LetterIndex++;
            }
            else
            {
                failed++;
            }

            onFileComplete?.Invoke(result);
            progress?.Report(new DownloadProgress(
                index + 1, files.Count, bytesDone, bytesTotal, file.FileName));
        }

        if (records.Count > 0)
            _history.AddRange(records);

        settings.DownloadsToday = sequences.DownloadsToday;
        settings.DownloadsTodayDate = DateTime.Today;
        settings.StoredSequenceNumber = sequences.StoredSequence;
        _settings.Save();

        return new DownloadSummary(
            succeeded, failed, skipped, bytesDone, DateTime.UtcNow - started);
    }

    private DownloadFileResult DownloadOne(
        SourceFile file,
        DownloadRequest request,
        SequenceState sequences,
        Dictionary<string, int> subfolderCounts,
        List<DownloadRecord> records,
        CancellationToken cancellation)
    {
        try
        {
            var isVideo = file.Kind == FileKind.Video;
            var root = isVideo ? request.VideoDestination : request.PhotoDestination;

            if (string.IsNullOrWhiteSpace(root))
            {
                return new DownloadFileResult
                {
                    File = file,
                    Success = false,
                    Error = isVideo
                        ? "No video destination folder is set."
                        : "No photo destination folder is set.",
                };
            }

            var metadata = _metadata.Read(file);
            var subfolderPreset = isVideo ? request.VideoSubfolderPreset : request.PhotoSubfolderPreset;
            var renamePreset = isVideo ? request.VideoRenamePreset : request.PhotoRenamePreset;

            var planningContext = new NameContext
            {
                File = file,
                Metadata = metadata,
                Sequences = sequences,
                JobCode = request.JobCode,
            };

            var subfolder = NameGenerator.GenerateSubfolder(subfolderPreset, planningContext);
            var targetFolder = string.IsNullOrEmpty(subfolder) ? root : Path.Combine(root, subfolder);

            // The subfolder counter restarts in every folder the run touches.
            subfolderCounts.TryGetValue(targetFolder, out var withinFolder);
            sequences.SubfolderSequence = withinFolder + 1;
            subfolderCounts[targetFolder] = sequences.SubfolderSequence;

            var fileName = NameGenerator.GenerateFileName(renamePreset, planningContext);
            Directory.CreateDirectory(targetFolder);

            var target = Path.Combine(targetFolder, fileName);
            var alreadyThere = File.Exists(target);

            if (alreadyThere && request.Conflict == ConflictResolution.SkipDownload)
            {
                // The copy is already at the destination, so the history should say so: the
                // next scan will recognise the file rather than offering it again.
                records.Add(BuildRecord(file, target, request));
                return new DownloadFileResult
                {
                    File = file,
                    Success = true,
                    Skipped = true,
                    DestinationPath = target,
                };
            }

            if (alreadyThere && request.Conflict == ConflictResolution.AddUniqueIdentifier)
                target = MakeUnique(target);

            CopyFile(file.FullPath, target, request.Verify, cancellation);

            var backups = CopySidecarsAndBackups(file, target, request, cancellation);

            if (request.Mode == TransferMode.Move)
                DeleteSource(file);

            records.Add(BuildRecord(file, target, request, backups));

            return new DownloadFileResult { File = file, Success = true, DestinationPath = target };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DownloadFileResult { File = file, Success = false, Error = ex.Message };
        }
    }

    private DownloadRecord BuildRecord(
        SourceFile file, string target, DownloadRequest request, string? backups = null) => new()
    {
        FileName = file.FileName,
        MTime = file.Identity.MTime,
        Size = file.Size,
        DownloadPath = target,
        DownloadedAtUtc = DateTime.UtcNow,
        SourceName = file.SourceName,
        SourcePath = file.FullPath,
        JobCode = request.JobCode,
        Kind = file.Kind,
        BackupPaths = backups,
    };

    /// <summary>
    /// Copy sidecars next to the renamed file, and write backup copies. Returns the backup
    /// paths, newline separated, for the history row.
    /// </summary>
    private static string? CopySidecarsAndBackups(
        SourceFile file, string target, DownloadRequest request, CancellationToken cancellation)
    {
        var targetStem = Path.Combine(
            Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target));

        foreach (var sidecar in file.Sidecars)
        {
            try
            {
                var extension = Path.GetExtension(sidecar);
                var sidecarTarget = MakeUnique(targetStem + extension);
                CopyFile(sidecar, sidecarTarget, verify: false, cancellation);
            }
            catch (Exception)
            {
                // A sidecar that will not copy must not fail the photo it belongs to.
            }
        }

        if (!request.BackupEnabled)
            return null;

        var backupRoot = file.Kind == FileKind.Video
            ? request.VideoBackupPath
            : request.PhotoBackupPath;

        if (string.IsNullOrWhiteSpace(backupRoot))
            return null;

        try
        {
            var primaryRoot = file.Kind == FileKind.Video
                ? request.VideoDestination
                : request.PhotoDestination;

            // Backups mirror the destination's folder structure and names exactly.
            var relative = Path.GetRelativePath(primaryRoot, target);
            var backupTarget = Path.Combine(backupRoot, relative);

            Directory.CreateDirectory(Path.GetDirectoryName(backupTarget)!);

            if (!File.Exists(backupTarget))
                CopyFile(file.FullPath, backupTarget, verify: false, cancellation);

            return backupTarget;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Copy to a temporary name first and rename on success, so an interrupted run never
    /// leaves a half-written file that looks complete.
    /// </summary>
    private static void CopyFile(
        string source, string target, bool verify, CancellationToken cancellation)
    {
        var temporary = target + ".sparkpart";

        try
        {
            byte[]? sourceHash;

            using (var input = new FileStream(
                       source, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16,
                       FileOptions.SequentialScan))
            {
                sourceHash = CopyStream(input, output, verify, cancellation);
            }

            File.SetLastWriteTimeUtc(temporary, File.GetLastWriteTimeUtc(source));

            if (verify && sourceHash is not null)
            {
                var targetHash = HashFile(temporary, cancellation);
                if (!sourceHash.AsSpan().SequenceEqual(targetHash))
                {
                    File.Delete(temporary);
                    throw new IOException(
                        $"Verification failed: the copy of {Path.GetFileName(source)} does not " +
                        "match the original.");
                }
            }

            File.Move(temporary, target, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
            catch (Exception)
            {
                // Leaving a stray part file behind is better than masking the real error.
            }

            throw;
        }
    }

    private static byte[]? CopyStream(
        Stream input, Stream output, bool hash, CancellationToken cancellation)
    {
        var buffer = new byte[1 << 20];
        using var hasher = hash ? SHA256.Create() : null;

        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            output.Write(buffer, 0, read);
            hasher?.TransformBlock(buffer, 0, read, null, 0);
        }

        if (hasher is null)
            return null;

        hasher.TransformFinalBlock([], 0, 0);
        return hasher.Hash;
    }

    private static byte[] HashFile(string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);
        using var sha = SHA256.Create();

        var buffer = new byte[1 << 20];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, read, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash!;
    }

    private static void DeleteSource(SourceFile file)
    {
        try
        {
            File.Delete(file.FullPath);
        }
        catch (Exception)
        {
            // A read-only card is a common, harmless reason for this to fail.
        }

        foreach (var sidecar in file.Sidecars)
        {
            try
            {
                File.Delete(sidecar);
            }
            catch (Exception)
            {
                // As above.
            }
        }
    }

    /// <summary>Append _1, _2 ... until the name is free.</summary>
    private static string MakeUnique(string path)
    {
        if (!File.Exists(path))
            return path;

        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var n = 1; n < 10_000; n++)
        {
            var candidate = Path.Combine(folder, $"{stem}_{n}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{stem}_{Guid.NewGuid():N}{extension}");
    }
}
