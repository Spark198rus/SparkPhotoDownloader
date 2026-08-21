using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.Services;

/// <summary>
/// Finds volumes that look like download sources: memory cards, camera volumes, phones and
/// external drives. Volumes are recognised by the camera folders they carry (DCIM and its
/// vendor variants), the way Rapid Photo Downloader recognises them.
/// </summary>
public sealed class DeviceScanner
{
    private readonly AppSettings _settings;

    public DeviceScanner(AppSettings settings) => _settings = settings;

    public IReadOnlyList<DownloadSource> ScanDevices()
    {
        var results = new List<DownloadSource>();

        foreach (var drive in SafeGetDrives())
        {
            // DriveType is read from a local table, while IsReady, the volume label and the
            // capacity figures all touch the device. Filtering on type first keeps a slow or
            // disconnected network share from stalling the whole scan.
            if (drive.DriveType is DriveType.Network or DriveType.CDRom or DriveType.Ram
                or DriveType.NoRootDirectory)
            {
                continue;
            }

            var root = drive.RootDirectory.FullName;

            if (_settings.IgnoredDevices.Contains(root, StringComparer.OrdinalIgnoreCase))
                continue;

            if (IsPseudoFileSystem(root, drive))
                continue;

            // Windows reports removable media honestly. Unix does not: .NET derives DriveType
            // from the file system name, which never yields Removable, so a card reads as
            // Fixed. There, a volume counts as external when it is mounted where the desktop
            // mounts removable media -- /Volumes on macOS, /media and friends on Linux.
            var removable = OperatingSystem.IsWindows()
                ? drive.DriveType is DriveType.Removable
                : IsUnixExternalMount(root);

            if (!SafeIsReady(drive))
                continue;

            if (!removable && (IsSystemDrive(root) || !_settings.IncludeFixedDrives))
                continue;

            var cameraFolder = FindCameraFolder(root);

            // On Unix DriveInfo.VolumeLabel just echoes the mount point, so the volume's real
            // name is the last segment of that path.
            var label = OperatingSystem.IsWindows()
                ? SafeVolumeLabel(drive)
                : UnixVolumeName(root);

            var display = root.TrimEnd(Path.DirectorySeparatorChar);
            if (display.Length == 0)
                display = root;

            var name = string.IsNullOrWhiteSpace(label) || label == display
                ? display
                : $"{label} ({display})";

            results.Add(new DownloadSource
            {
                Id = "device:" + root,
                Name = name,
                RootPath = root,
                Kind = ClassifyDevice(cameraFolder, removable),
                HasCameraFolder = cameraFolder is not null,
                IsRemovable = removable,
                TotalBytes = SafeSize(() => drive.TotalSize),
                FreeBytes = SafeSize(() => drive.AvailableFreeSpace),
                Description = SafeFormat(drive),
            });
        }

        return results;
    }

    /// <summary>Mount points where macOS and Linux desktops attach removable media.</summary>
    private static readonly string[] UnixExternalMountRoots =
        ["/Volumes/", "/media/", "/run/media/", "/mnt/"];

    private static bool IsUnixExternalMount(string root) =>
        UnixExternalMountRoots.Any(prefix =>
            root.StartsWith(prefix, StringComparison.Ordinal) && root.Length > prefix.Length);

    /// <summary>
    /// Kernel and system mounts that appear in the drive list on Unix but hold nothing a
    /// person would download: /dev, /proc, macOS's sealed system volumes, and so on.
    /// </summary>
    private static bool IsPseudoFileSystem(string root, DriveInfo drive)
    {
        if (OperatingSystem.IsWindows())
            return false;

        string[] systemPrefixes =
        [
            "/dev", "/proc", "/sys", "/run/lock", "/run/user", "/private/var/vm",
            "/System/Volumes", "/Library/Developer", "/snap", "/var/lib/docker",
        ];

        if (systemPrefixes.Any(p => root.StartsWith(p, StringComparison.Ordinal)))
            return true;

        string[] pseudoFormats =
        [
            "devfs", "autofs", "sysfs", "proc", "tmpfs", "ramfs", "overlay", "squashfs",
            "cgroup", "cgroup2", "devtmpfs", "fusectl", "securityfs", "debugfs", "tracefs",
            "binfmt_misc", "configfs", "pstore", "efivarfs", "mqueue", "hugetlbfs", "nsfs",
        ];

        var format = SafeFormat(drive);
        return format is not null &&
               pseudoFormats.Contains(format, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The volume's own name on Unix, which is the last segment of its mount point.</summary>
    private static string? UnixVolumeName(string root)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var trimmed = root.TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');
        return slash >= 0 && slash < trimmed.Length - 1 ? trimmed[(slash + 1)..] : null;
    }

    /// <summary>Turn a hand-picked folder into a source for the "This Computer" panel.</summary>
    public static DownloadSource CreateFolderSource(string path)
    {
        var full = Path.GetFullPath(path);
        long total = 0, free = 0;

        try
        {
            var root = Path.GetPathRoot(full);
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    total = drive.TotalSize;
                    free = drive.AvailableFreeSpace;
                }
            }
        }
        catch (Exception)
        {
            // Size is decoration; a folder with unknown capacity is still a valid source.
        }

        return new DownloadSource
        {
            Id = "folder:" + full,
            Name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n
                ? n
                : full,
            RootPath = full,
            Kind = DeviceKind.LocalFolder,
            HasCameraFolder = FindCameraFolder(full) is not null,
            IsRemovable = false,
            TotalBytes = total,
            FreeBytes = free,
            Description = full,
        };
    }

    private static DeviceKind ClassifyDevice(string? cameraFolder, bool removable)
    {
        if (cameraFolder is null)
            return removable ? DeviceKind.MemoryCard : DeviceKind.ExternalDrive;

        var folder = Path.GetFileName(cameraFolder).ToUpperInvariant();
        return folder switch
        {
            "DCIM" => removable ? DeviceKind.MemoryCard : DeviceKind.Camera,
            "AVCHD" or "PRIVATE" or "M4ROOT" or "XDROOT" => DeviceKind.Camera,
            _ => DeviceKind.MemoryCard,
        };
    }

    /// <summary>Returns the first camera folder found at the volume root, or null.</summary>
    public static string? FindCameraFolder(string root)
    {
        try
        {
            foreach (var candidate in FileTypeRegistry.CameraFolderNames)
            {
                var path = Path.Combine(root, candidate);
                if (Directory.Exists(path))
                    return path;
            }
        }
        catch (Exception)
        {
            // Unreadable roots simply do not advertise a camera folder.
        }

        return null;
    }

    private static bool IsSystemDrive(string root)
    {
        if (!OperatingSystem.IsWindows())
            return root is "/" or "";

        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System));
        return string.Equals(root, system, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeFormat(DriveInfo drive)
    {
        try
        {
            return drive.DriveFormat;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IEnumerable<DriveInfo> SafeGetDrives()
    {
        try
        {
            return DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static bool SafeIsReady(DriveInfo drive)
    {
        try
        {
            return drive.IsReady;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? SafeVolumeLabel(DriveInfo drive)
    {
        try
        {
            return drive.VolumeLabel;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long SafeSize(Func<long> get)
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
}
