using SparkPhotoDownloader.Models;
#if WINDOWS
using System.Collections.Concurrent;
using MediaDevices;
#endif

namespace SparkPhotoDownloader.Services;

/// <summary>One photo or video found on a portable device.</summary>
/// <param name="DevicePath">The file's path on the device, used to open it again.</param>
/// <param name="DisplayFolder">The containing folder below the storage, for display.</param>
public readonly record struct DeviceMediaFile(
    string DevicePath, string DisplayFolder, string Name, long Size, DateTime ModifiedUtc);

/// <summary>
/// Phones and cameras connected over MTP ("File transfer") or PTP. These are not mounted as
/// volumes, so DriveInfo never sees them: Android phones in both USB modes, and cameras set
/// to PTP. On Windows they are reached through Windows Portable Devices — the interface
/// Explorer uses for "This PC → phone" — via the MediaDevices library. Elsewhere there is no
/// support yet and every member reports nothing.
/// </summary>
public static class PortableDevices
{
    /// <summary>The folders photos and videos live in on a phone's storage.</summary>
    private static readonly string[] MediaFolders = ["DCIM", "Pictures", "Movies"];

    public static bool IsSupported => OperatingSystem.IsWindows();

#if WINDOWS
    /// <summary>
    /// Connected devices, kept open for the session. MediaDevices 2 is thread-safe, so the
    /// scanner, the thumbnail loader and the download engine share one connection.
    /// </summary>
    private static readonly ConcurrentDictionary<string, MediaDevice> Connected = new();

    /// <summary>Devices that turned out to be ordinary drives, so they are not probed again.</summary>
    private static readonly ConcurrentDictionary<string, bool> NotPortable = new();

    /// <summary>
    /// Phones and cameras connected now. Each becomes a download source with an id of
    /// "mtp:" plus the device id. Cards and USB drives also appear in Windows Portable
    /// Devices; they are left out, because DriveInfo already lists them with a drive letter.
    /// </summary>
    public static IReadOnlyList<DownloadSource> ListDevices()
    {
        var results = new List<DownloadSource>();
        IEnumerable<MediaDevice>? devices;

        try
        {
            devices = MediaDeviceManager.Instance.GetDevices();
        }
        catch (Exception)
        {
            return results;
        }

        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in devices ?? [])
        {
            var id = candidate.DeviceId;
            present.Add(id);

            if (NotPortable.ContainsKey(id))
                continue;

            var device = Connected.GetOrAdd(id, _ => candidate);
            if (!ReferenceEquals(device, candidate))
                candidate.Dispose();

            try
            {
                if (!device.IsConnected)
                    device.Connect(MediaDeviceAccess.Default, MediaDeviceShare.Default, enableCache: true);

                // "MTP: 1.00" or "PTP: 1.00"; mass-storage devices report "MSC" or nothing.
                var protocol = device.Protocol ?? string.Empty;
                if (!protocol.Contains("MTP", StringComparison.OrdinalIgnoreCase) &&
                    !protocol.Contains("PTP", StringComparison.OrdinalIgnoreCase))
                {
                    NotPortable[id] = true;
                    Forget(id);
                    continue;
                }

                long total = 0, free = 0;
                foreach (var drive in device.GetDrives() ?? [])
                {
                    total += SafeLong(() => drive.TotalSize);
                    free += SafeLong(() => drive.AvailableFreeSpace);
                }

                var name = FirstNonEmpty(device.FriendlyName, device.Description, device.Model, "Phone");

                results.Add(new DownloadSource
                {
                    Id = "mtp:" + id,
                    Name = name,
                    RootPath = name,
                    DeviceId = id,
                    Kind = DeviceKind.Phone,
                    HasCameraFolder = true,
                    IsRemovable = true,
                    TotalBytes = total,
                    FreeBytes = free,
                    Description = protocol.Length > 0
                        ? $"{FirstNonEmpty(device.Manufacturer, string.Empty)} · {protocol}".Trim(' ', '·')
                        : device.Manufacturer,
                });
            }
            catch (Exception)
            {
                // A phone that is still asking "Allow access to phone data?" refuses the
                // connection; it appears on the next poll once the user has allowed it.
                Forget(id);
            }
        }

        // Unplugged devices: release their connections.
        foreach (var id in Connected.Keys.Where(k => !present.Contains(k)).ToList())
            Forget(id);

        return results;
    }

    /// <summary>
    /// Every photo and video under the DCIM, Pictures and Movies folders of each storage on
    /// the device. Walking the whole of a phone's storage over MTP takes minutes; these are
    /// where Android, iOS and cameras put pictures.
    /// </summary>
    public static IEnumerable<DeviceMediaFile> EnumerateMedia(
        string deviceId, CancellationToken cancellation)
    {
        if (!Connected.TryGetValue(deviceId, out var device))
            yield break;

        foreach (var drive in device.GetDrives() ?? [])
        {
            var root = drive.RootDirectory;
            if (root is null)
                continue;

            foreach (var folder in SafeDirectories(root))
            {
                if (!MediaFolders.Contains(folder.Name, StringComparer.OrdinalIgnoreCase))
                    continue;

                foreach (var file in Walk(folder, cancellation))
                {
                    cancellation.ThrowIfCancellationRequested();

                    var extension = Path.GetExtension(file.Name).TrimStart('.').ToLowerInvariant();
                    var kind = FileTypeRegistry.Classify(extension);
                    if (!FileTypeRegistry.IsDownloadable(kind))
                        continue;

                    var modified = file.LastWriteTime ?? file.DateAuthored ?? file.CreationTime ?? DateTime.Now;
                    var parent = Path.GetDirectoryName(file.FullName) ?? string.Empty;

                    yield return new DeviceMediaFile(
                        file.FullName,
                        RelativeTo(root.FullName, parent),
                        file.Name,
                        (long)file.Length,
                        modified.Kind == DateTimeKind.Utc ? modified : modified.ToUniversalTime());
                }
            }
        }
    }

    /// <summary>Open a file on the device for reading.</summary>
    public static Stream OpenRead(string deviceId, string devicePath)
    {
        var device = Device(deviceId);
        var info = device.GetFileInfo(devicePath)
            ?? throw new FileNotFoundException("The file is no longer on the device.", devicePath);
        return info.OpenRead();
    }

    /// <summary>
    /// The thumbnail the device keeps for a file, when it has one. Android serves the
    /// picture's Exif thumbnail, about 160 to 512 pixels wide; videos usually have none.
    /// </summary>
    public static byte[]? ReadThumbnail(string deviceId, string devicePath)
    {
        try
        {
            using var thumbnail = Device(deviceId).GetFileInfo(devicePath)?.OpenThumbnail();
            if (thumbnail is null)
                return null;

            using var memory = new MemoryStream();
            thumbnail.CopyTo(memory);
            return memory.Length > 0 ? memory.ToArray() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The first <paramref name="length"/> bytes of a file: enough for the Exif block of a
    /// JPEG or HEIC, read without pulling the whole picture over USB.
    /// </summary>
    public static byte[]? ReadHead(string deviceId, string devicePath, int length)
    {
        try
        {
            using var stream = OpenRead(deviceId, devicePath);
            var buffer = new byte[length];
            var total = 0;
            int read;
            while (total < length && (read = stream.Read(buffer, total, length - total)) > 0)
                total += read;

            return total == length ? buffer : buffer[..total];
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Delete a file from the device, for Move.</summary>
    public static void Delete(string deviceId, string devicePath) =>
        Device(deviceId).DeleteFile(devicePath);

    private static MediaDevice Device(string deviceId) =>
        Connected.TryGetValue(deviceId, out var device)
            ? device
            : throw new IOException("The phone or camera is no longer connected.");

    private static void Forget(string deviceId)
    {
        if (!Connected.TryRemove(deviceId, out var device))
            return;

        try
        {
            if (device.IsConnected)
                device.Disconnect();
            device.Dispose();
        }
        catch (Exception)
        {
            // Unplugged mid-call; nothing left to release.
        }
    }

    private static IEnumerable<MediaFileInfo> Walk(MediaDirectoryInfo folder, CancellationToken cancellation)
    {
        var pending = new Stack<MediaDirectoryInfo>();
        pending.Push(folder);

        while (pending.Count > 0)
        {
            cancellation.ThrowIfCancellationRequested();
            var current = pending.Pop();

            // Android keeps generated thumbnails beside the pictures; they are not photos.
            if (current.Name.Equals(".thumbnails", StringComparison.OrdinalIgnoreCase))
                continue;

            List<MediaFileInfo> files;
            try
            {
                files = current.EnumerateFiles().ToList();
            }
            catch (Exception)
            {
                files = [];
            }

            foreach (var file in files)
                yield return file;

            foreach (var child in SafeDirectories(current))
                pending.Push(child);
        }
    }

    private static List<MediaDirectoryInfo> SafeDirectories(MediaDirectoryInfo folder)
    {
        try
        {
            return folder.EnumerateDirectories().ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string RelativeTo(string root, string path)
    {
        var trimmedRoot = root.TrimEnd('\\');
        return path.StartsWith(trimmedRoot, StringComparison.OrdinalIgnoreCase)
            ? path[trimmedRoot.Length..].TrimStart('\\')
            : path;
    }

    private static long SafeLong(Func<long> get)
    {
        try
        {
            return get();
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;
#else
    public static IReadOnlyList<DownloadSource> ListDevices() => [];

    public static IEnumerable<DeviceMediaFile> EnumerateMedia(string deviceId, CancellationToken cancellation) => [];

    public static Stream OpenRead(string deviceId, string devicePath) =>
        throw new PlatformNotSupportedException("Phones over MTP/PTP are only supported on Windows.");

    public static byte[]? ReadThumbnail(string deviceId, string devicePath) => null;

    public static byte[]? ReadHead(string deviceId, string devicePath, int length) => null;

    public static void Delete(string deviceId, string devicePath) =>
        throw new PlatformNotSupportedException("Phones over MTP/PTP are only supported on Windows.");
#endif
}

/// <summary>
/// Opens a source file wherever it is: a path on a mounted volume, or a file on a phone.
/// </summary>
public static class SourceAccess
{
    public static Stream OpenRead(SourceFile file) =>
        file is { DeviceId: { } device, DevicePath: { } path }
            ? PortableDevices.OpenRead(device, path)
            : new FileStream(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                1 << 16, FileOptions.SequentialScan);
}
