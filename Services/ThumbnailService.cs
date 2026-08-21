using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using SparkPhotoDownloader.Models;
using Directory = System.IO.Directory;

namespace SparkPhotoDownloader.Services;

/// <summary>
/// Produces the preview images shown in the middle of the window, and caches them on disk so
/// that re-inserting the same card is fast.
/// </summary>
/// <remarks>
/// Ordinary images are decoded directly. Raw files are not decoded at all: every raw format
/// carries one or more embedded JPEG previews, and the largest of those is extracted and used,
/// which is both far quicker than demosaicing and what the camera itself would show. Videos
/// use a THM sidecar when the camera wrote one, otherwise a frame pulled with ffmpeg when it
/// is installed.
/// </remarks>
public sealed class ThumbnailService
{
    private readonly AppSettings _settings;
    private readonly string _cacheDirectory;
    private readonly Lazy<string?> _ffmpeg;

    /// <summary>
    /// How far into a file to look for an embedded preview before giving up. Every raw format
    /// puts its previews in front of the sensor data, so this is a backstop for an unfamiliar
    /// layout rather than a figure normally reached -- the scan stops as soon as it has a
    /// usable preview, typically inside the first two megabytes.
    /// </summary>
    private const long MaxScanBytes = 24L * 1024 * 1024;

    /// <summary>Ignore embedded JPEGs smaller than this: they are the tiny 160px thumbnails.</summary>
    private const int MinPreviewBytes = 6 * 1024;

    /// <summary>
    /// A preview this much wider than the grid cell is plenty; scanning stops once one is
    /// found. The margin leaves room for the size slider without a rescan.
    /// </summary>
    private const int PreviewWidthFactor = 3;

    /// <summary>How many candidate blobs are worth reading back to check before giving up.</summary>
    private const int MaxValidationReads = 24;

    /// <summary>
    /// How much of a candidate to read when deciding whether it is a real image. Enough to
    /// clear a full-size Exif block and still reach the frame header behind it.
    /// </summary>
    private const int PreviewHeaderPeekBytes = 128 * 1024;

    /// <summary>
    /// How many unclosed start markers to remember. More than a couple means the file is
    /// noisy rather than nested, and the extra ones are not worth chasing.
    /// </summary>
    private const int MaxPendingStarts = 8;

    public ThumbnailService(AppSettings settings)
    {
        _settings = settings;
        _cacheDirectory = AppPaths.ThumbnailCacheDirectory;
        Directory.CreateDirectory(_cacheDirectory);
        _ffmpeg = new Lazy<string?>(FindFfmpeg, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public bool FfmpegAvailable => _ffmpeg.Value is not null;

    /// <summary>
    /// Load or build the thumbnail for a file. Returns null when no preview can be produced,
    /// in which case the UI shows a type placeholder instead.
    /// </summary>
    public ThumbnailResult Load(SourceFile file, int size, CancellationToken cancellation = default)
    {
        var cachePath = CachePath(file, size);

        if (File.Exists(cachePath))
        {
            var cached = TryDecodeFile(cachePath);
            if (cached is not null)
                return new ThumbnailResult(cached, 1, true, cachePath);
        }

        cancellation.ThrowIfCancellationRequested();

        var (bitmap, orientation) = Build(file, size, cancellation);
        if (bitmap is null)
            return ThumbnailResult.None;

        // Caching is deliberately left to the caller: a rotated thumbnail has to be turned
        // upright first, or the cached copy would come back sideways next session.
        if (orientation <= 1)
            Cache(bitmap, cachePath);

        return new ThumbnailResult(bitmap, orientation, false, cachePath);
    }

    /// <summary>Write a finished thumbnail to the on-disk cache.</summary>
    public void Cache(Bitmap bitmap, string? cachePath)
    {
        if (cachePath is null)
            return;

        try
        {
            bitmap.Save(cachePath, PngOptions);
        }
        catch (Exception)
        {
            // A cache miss next time is the only cost.
        }
    }

    /// <summary>
    /// Thumbnails are cached as PNG. Compression is kept low: these are small images that get
    /// written once and read many times, so decode speed matters more than a few kilobytes.
    /// </summary>
    private static readonly PngBitmapEncoderOptions PngOptions = new()
    {
        CompressionLevel = System.IO.Compression.CompressionLevel.Fastest,
    };

    private (Bitmap? Bitmap, int Orientation) Build(
        SourceFile file, int size, CancellationToken cancellation)
    {
        try
        {
            if (file.Kind == FileKind.Video)
                return (BuildVideoThumbnail(file, size, cancellation), 1);

            if (FileTypeRegistry.IsDirectlyDecodable(file.Extension))
                return (TryDecodeFile(file.FullPath, size), ReadOrientation(file.FullPath));

            // Raw and HEIF-family files: use the preview the camera embedded.
            var preview = ExtractEmbeddedPreview(
                file.FullPath, size * PreviewWidthFactor, cancellation);
            if (preview is not null)
            {
                using var stream = new MemoryStream(preview);
                var bitmap = TryDecodeStream(stream, size);
                if (bitmap is not null)
                    return (bitmap, ReadOrientation(file.FullPath));
            }

            // Some DNGs are plain enough that the normal decoder copes.
            return (TryDecodeFile(file.FullPath, size), ReadOrientation(file.FullPath));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return (null, 1);
        }
    }

    // -------------------------------------------------------------------------------------
    // Embedded preview extraction
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Find the preview image a raw file carries, without reading the raw data itself.
    /// </summary>
    /// <remarks>
    /// Raw containers hold several JPEGs: a postage-stamp Exif thumbnail, and a display
    /// preview of a megapixel or two. Both sit near the front of the file, before the sensor
    /// data. Two things follow, and this method depends on both.
    ///
    /// The scan stops as soon as it has a preview big enough to fill the grid cell, rather
    /// than reading to the end of the file. On a 36 MB frame the preview is reached within
    /// about 1.2 MB, so the cost per file drops by an order of magnitude and the card is no
    /// longer the bottleneck.
    ///
    /// Every candidate is validated by parsing its frame header. Sensor data readily contains
    /// the byte pairs FF D8 FF and FF D9 by coincidence, and those false positives are often
    /// *larger* than the real preview -- picking the biggest blob without checking it means
    /// decoding noise, or failing and showing a placeholder for a file that had a perfectly
    /// good preview all along.
    /// </remarks>
    /// <param name="desiredWidth">
    /// The width the grid needs. Scanning stops once a valid preview at least this wide has
    /// been found; anything larger is only more to decode and downscale.
    /// </param>
    private static byte[]? ExtractEmbeddedPreview(
        string path, int desiredWidth, CancellationToken cancellation)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan);

        var limit = Math.Min(stream.Length, MaxScanBytes);
        var buffer = new byte[1 << 20];
        var carry = new byte[3];
        var carryLength = 0;
        long absolute = 0;

        // Several start markers can precede a single end marker: raw metadata blocks contain
        // stray FF D8 FF sequences, and the genuine preview often begins *after* one of them.
        // Tracking only the first start would build a segment from the wrong offset and hide
        // the real preview behind it, so every open start is kept and tried.
        var pendingStarts = new List<long>();

        byte[]? best = null;
        var bestWidth = 0;
        var validations = 0;

        while (absolute < limit)
        {
            cancellation.ThrowIfCancellationRequested();

            var toRead = (int)Math.Min(buffer.Length - carryLength, limit - absolute);
            if (toRead <= 0)
                break;

            Array.Copy(carry, 0, buffer, 0, carryLength);
            var read = stream.Read(buffer, carryLength, toRead);
            if (read <= 0)
                break;

            var available = carryLength + read;
            var windowStart = absolute - carryLength;
            var span = buffer.AsSpan(0, available);

            for (var i = 0; i + 1 < available; i++)
            {
                // Jump to the next marker byte instead of testing every byte in turn.
                var next = span[i..].IndexOf((byte)0xFF);
                if (next < 0)
                    break;

                i += next;
                if (i + 1 >= available)
                    break;

                // Start of a JPEG: FF D8 FF
                if (span[i + 1] == 0xD8 && i + 2 < available && span[i + 2] == 0xFF)
                {
                    if (pendingStarts.Count < MaxPendingStarts)
                        pendingStarts.Add(windowStart + i);
                    continue;
                }

                // End of a JPEG: FF D9. Every start still open ends here, so each is checked
                // and the widest image among them wins.
                if (span[i + 1] != 0xD9 || pendingStarts.Count == 0)
                    continue;

                var end = windowStart + i + 2;

                foreach (var start in pendingStarts)
                {
                    var length = end - start;
                    if (length < MinPreviewBytes || length > int.MaxValue)
                        continue;

                    // Cap the work a file full of coincidental markers can cause.
                    if (++validations > MaxValidationReads)
                        break;

                    // Check the frame header before committing to the whole blob: it sits
                    // near the front, so a short read settles whether this is a real image
                    // and how big it is. Only a winner is read in full.
                    var peekLength = (int)Math.Min(length, PreviewHeaderPeekBytes);
                    var header = ReadRange(stream, start, peekLength, absolute + read);
                    if (header is null)
                        continue;

                    var width = JpegFrameWidth(header);
                    if (width <= bestWidth)
                        continue;

                    var candidate = length == peekLength
                        ? header
                        : ReadRange(stream, start, (int)length, absolute + read);

                    if (candidate is null)
                        continue;

                    best = candidate;
                    bestWidth = width;
                }

                pendingStarts.Clear();
            }

            absolute += read;

            if (best is not null && bestWidth >= desiredWidth)
                break;

            carryLength = Math.Min(3, available);
            Array.Copy(buffer, available - carryLength, carry, 0, carryLength);
        }

        return best;
    }

    /// <summary>
    /// Read a byte range, restoring the sequential read position afterwards so the enclosing
    /// scan carries on where it left off.
    /// </summary>
    private static byte[]? ReadRange(FileStream stream, long start, int length, long resumeAt)
    {
        try
        {
            var data = new byte[length];
            stream.Seek(start, SeekOrigin.Begin);
            stream.ReadExactly(data, 0, length);
            return data;
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            stream.Seek(resumeAt, SeekOrigin.Begin);
        }
    }

    /// <summary>
    /// Whether a frame header's dimensions could belong to a camera's embedded preview.
    /// </summary>
    /// <remarks>
    /// Sensor noise occasionally produces a marker chain that parses cleanly but describes an
    /// image no camera would write -- 18000 pixels across, or a sliver a few pixels tall.
    /// Accepting one is worse than finding nothing, because the scan stops there and the
    /// genuine preview further on is never reached. The ceiling clears the largest sensors in
    /// use; the ratio limit is far wider than any real frame, panoramas included.
    /// </remarks>
    private static bool IsPlausiblePreview(int width, int height)
    {
        if (width is < 32 or > 12000 || height is < 32 or > 12000)
            return false;

        var ratio = (double)width / height;
        return ratio is >= 0.125 and <= 8.0;
    }

    /// <summary>
    /// The width in a JPEG's frame header, or 0 when the bytes are not a real JPEG.
    /// </summary>
    /// <remarks>
    /// This is what separates a genuine embedded preview from sensor data that merely starts
    /// with the right two bytes: a real JPEG has an intact marker chain leading to an SOF
    /// segment with plausible dimensions.
    /// </remarks>
    private static int JpegFrameWidth(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return 0;

        var i = 2;
        while (i + 3 < data.Length)
        {
            if (data[i] != 0xFF)
                return 0;

            var marker = data[i + 1];

            // Padding, restart markers and a second SOI carry no length field.
            if (marker == 0xFF || marker == 0x01 || marker is >= 0xD0 and <= 0xD8)
            {
                i += marker == 0xFF ? 1 : 2;
                continue;
            }

            if (marker == 0xD9)
                return 0;

            var length = (data[i + 2] << 8) | data[i + 3];
            if (length < 2)
                return 0;

            // SOF0..SOF15, less the markers that share the range but are not frame headers.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                if (i + 9 >= data.Length)
                    return 0;

                var height = (data[i + 5] << 8) | data[i + 6];
                var width = (data[i + 7] << 8) | data[i + 8];

                return IsPlausiblePreview(width, height) ? width : 0;
            }

            i += 2 + length;
        }

        return 0;
    }

    // -------------------------------------------------------------------------------------
    // Videos
    // -------------------------------------------------------------------------------------

    private Bitmap? BuildVideoThumbnail(SourceFile file, int size, CancellationToken cancellation)
    {
        // Many cameras write a matching .THM next to the clip: free, instant, correct.
        var thm = file.Sidecars.FirstOrDefault(
            s => FileTypeRegistry.NormalizeExtension(s) is "thm" or "jpg");
        if (thm is not null)
        {
            var fromSidecar = TryDecodeFile(thm, size);
            if (fromSidecar is not null)
                return fromSidecar;
        }

        // Some containers carry a poster frame as an embedded JPEG.
        var embedded = ExtractEmbeddedPreview(
            file.FullPath, size * PreviewWidthFactor, cancellation);
        if (embedded is not null)
        {
            using var stream = new MemoryStream(embedded);
            var poster = TryDecodeStream(stream, size);
            if (poster is not null)
                return poster;
        }

        if (_settings.UseFfmpegForVideoThumbnails && _ffmpeg.Value is { } ffmpeg)
            return ExtractFrameWithFfmpeg(ffmpeg, file.FullPath, size, cancellation);

        return null;
    }

    private static Bitmap? ExtractFrameWithFfmpeg(
        string ffmpeg, string path, int size, CancellationToken cancellation)
    {
        try
        {
            var info = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var argument in new[]
                     {
                         "-hide_banner", "-loglevel", "error",
                         "-ss", "1",
                         "-i", path,
                         "-frames:v", "1",
                         "-vf", $"scale={size * 2}:-1",
                         "-f", "mjpeg", "-",
                     })
            {
                info.ArgumentList.Add(argument);
            }

            using var process = Process.Start(info);
            if (process is null)
                return null;

            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);

            if (!process.WaitForExit(15_000))
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
                return null;
            }

            cancellation.ThrowIfCancellationRequested();

            if (output.Length == 0)
                return null;

            output.Position = 0;
            return TryDecodeStream(output, size);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FindFfmpeg()
    {
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";

        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var directory in directories)
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (Exception)
            {
                // A malformed PATH entry is not worth reporting.
            }
        }

        return null;
    }

    // -------------------------------------------------------------------------------------
    // Decoding, orientation and caching
    // -------------------------------------------------------------------------------------

    private static Bitmap? TryDecodeFile(string path, int? width = null)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return TryDecodeStream(stream, width);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Bitmap? TryDecodeStream(Stream stream, int? width)
    {
        try
        {
            return width is { } w
                ? Bitmap.DecodeToWidth(stream, w, BitmapInterpolationMode.MediumQuality)
                : new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Exif orientation of a file, or 1 when it has none.</summary>
    private static int ReadOrientation(string path)
    {
        try
        {
            var directories = ImageMetadataReader.ReadMetadata(path);
            foreach (var directory in directories.OfType<ExifIfd0Directory>())
            {
                if (directory.TryGetInt32(ExifDirectoryBase.TagOrientation, out var value))
                    return value is >= 1 and <= 8 ? value : 1;
            }
        }
        catch (Exception)
        {
            // No metadata means no rotation.
        }

        return 1;
    }

    /// <summary>
    /// Rotate and flip a decoded thumbnail to match its Exif orientation. Must run on the UI
    /// thread because it renders through the platform's drawing stack.
    /// </summary>
    public static Bitmap ApplyOrientation(Bitmap source, int orientation)
    {
        if (orientation is <= 1 or > 8)
            return source;

        var width = source.PixelSize.Width;
        var height = source.PixelSize.Height;
        var rotated = orientation is 5 or 6 or 7 or 8;
        var target = new PixelSize(rotated ? height : width, rotated ? width : height);

        var transform = orientation switch
        {
            2 => Matrix.CreateScale(-1, 1) * Matrix.CreateTranslation(width, 0),
            3 => Matrix.CreateRotation(Math.PI) * Matrix.CreateTranslation(width, height),
            4 => Matrix.CreateScale(1, -1) * Matrix.CreateTranslation(0, height),
            5 => Matrix.CreateScale(-1, 1) * Matrix.CreateRotation(-Math.PI / 2)
                 * Matrix.CreateTranslation(0, 0),
            6 => Matrix.CreateRotation(Math.PI / 2) * Matrix.CreateTranslation(height, 0),
            7 => Matrix.CreateScale(-1, 1) * Matrix.CreateRotation(Math.PI / 2)
                 * Matrix.CreateTranslation(height, width) * Matrix.CreateScale(1, -1)
                 * Matrix.CreateTranslation(0, width),
            8 => Matrix.CreateRotation(-Math.PI / 2) * Matrix.CreateTranslation(0, width),
            _ => Matrix.Identity,
        };

        try
        {
            var render = new RenderTargetBitmap(target, new Vector(96, 96));
            using (var context = render.CreateDrawingContext())
            using (context.PushTransform(transform))
            {
                context.DrawImage(source, new Rect(0, 0, width, height));
            }

            source.Dispose();
            return render;
        }
        catch (Exception)
        {
            return source;
        }
    }


    /// <summary>
    /// Cache key: full path, modification time and size, so an edited or replaced file gets a
    /// fresh thumbnail while an unchanged one is reused across sessions.
    /// </summary>
    private string CachePath(SourceFile file, int size)
    {
        var key = $"{file.FullPath}|{file.ModifiedUtc.Ticks}|{file.Size}|{size}";
        var hash = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(_cacheDirectory, hash + ".png");
    }

    /// <summary>Delete every cached thumbnail. Offered in Preferences.</summary>
    public (int Files, long Bytes) ClearCache()
    {
        var count = 0;
        long bytes = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(_cacheDirectory, "*.png"))
            {
                try
                {
                    bytes += new FileInfo(path).Length;
                    File.Delete(path);
                    count++;
                }
                catch (Exception)
                {
                    // Skip files still held open by the UI.
                }
            }
        }
        catch (Exception)
        {
            // Nothing to clear.
        }

        return (count, bytes);
    }

    public (int Files, long Bytes) CacheSize()
    {
        try
        {
            var files = Directory.EnumerateFiles(_cacheDirectory, "*.png").ToList();
            return (files.Count, files.Sum(f => new FileInfo(f).Length));
        }
        catch (Exception)
        {
            return (0, 0);
        }
    }
}

/// <summary>The outcome of a thumbnail request.</summary>
/// <param name="Bitmap">The image, or null when none could be produced.</param>
/// <param name="Orientation">Exif orientation still to be applied, 1 when none.</param>
/// <param name="FromCache">True when it came off disk, in which case it is already upright.</param>
/// <param name="CachePath">Where to write it once orientation has been applied.</param>
public readonly record struct ThumbnailResult(
    Bitmap? Bitmap, int Orientation, bool FromCache, string? CachePath = null)
{
    public static readonly ThumbnailResult None = new(null, 1, false);

    /// <summary>True when the caller still has to rotate the image and cache the result.</summary>
    public bool NeedsOrientation => !FromCache && Orientation is > 1 and <= 8;
}
