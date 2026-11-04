using System.Runtime.InteropServices;

namespace SparkPhotoDownloader.Services;

/// <summary>
/// Works out which volume a path lives on.
/// <para>
/// <see cref="Path.GetPathRoot(string)"/> answers that on Windows, where every volume has a
/// root of its own. On macOS and Linux it always returns "/", so asking it about
/// /Volumes/Server/Photos reports the start-up disk: its free space, and whether it is
/// mounted. Here the path itself is asked instead.
/// </para>
/// </summary>
public static class VolumeInfo
{
    /// <summary>
    /// Capacity and free space of the volume a file written to <paramref name="path"/> would
    /// land on. The folder need not exist yet: its nearest existing ancestor is measured.
    /// </summary>
    public static bool TryGetSpace(string path, out long totalBytes, out long freeBytes)
    {
        totalBytes = 0;
        freeBytes = 0;

        try
        {
            var existing = NearestExistingDirectory(Path.GetFullPath(path));
            if (existing is null)
                return false;

            // On Unix DriveInfo accepts any path and measures it with statfs, which reports
            // the file system that path is actually on -- a network share, a card, an
            // external disk -- and follows symbolic links on the way.
            var drive = OperatingSystem.IsWindows()
                ? new DriveInfo(Path.GetPathRoot(existing)!)
                : new DriveInfo(existing);

            if (!drive.IsReady)
                return false;

            totalBytes = drive.TotalSize;
            freeBytes = drive.AvailableFreeSpace;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// The folder a removable or network volume is mounted on, for a path somewhere inside
    /// it: /Volumes/Name on macOS, /media/user/Name, /run/media/user/Name or /mnt/Name on
    /// Linux, the drive or share root on Windows. A path on the start-up disk gives its root.
    /// </summary>
    public static string VolumeKey(string fullPath)
    {
        if (OperatingSystem.IsWindows())
            return Path.GetPathRoot(fullPath) ?? string.Empty;

        var parts = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // How many leading segments name the volume: /Volumes/Name, /mnt/Name,
        // /media/user/Name, /run/media/user/Name.
        var depth = parts switch
        {
            ["Volumes", _, ..] or ["mnt", _, ..] => 2,
            ["media", _, _, ..] => 3,
            ["run", "media", _, _, ..] => 4,
            _ => 0,
        };

        return depth == 0 ? "/" : "/" + string.Join('/', parts[..depth]);
    }

    /// <summary>
    /// True for volumes macOS keeps out of sight: Recovery, Preboot, VM, Update and the rest
    /// of an APFS container's system roles, and anything mounted "nobrowse", which is how
    /// the system tells Finder not to show a volume.
    /// </summary>
    public static bool IsHiddenMacSystemVolume(string root)
    {
        if (!OperatingSystem.IsMacOS())
            return false;

        var trimmed = root.TrimEnd('/');
        if (trimmed.StartsWith("/Volumes/", StringComparison.Ordinal))
        {
            var name = trimmed["/Volumes/".Length..];
            if (MacSystemVolumeNames.Contains(name))
                return true;
        }

        return MacMountFlags(trimmed.Length == 0 ? "/" : trimmed) is { } flags &&
               (flags & MNT_DONTBROWSE) != 0;
    }

    /// <summary>Names macOS gives the system-role volumes of an APFS container.</summary>
    private static readonly HashSet<string> MacSystemVolumeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Recovery", "Preboot", "VM", "Update", "xarts", "iSCPreboot", "Hardware",
    };

    private static string? NearestExistingDirectory(string fullPath)
    {
        for (var dir = fullPath; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            if (Directory.Exists(dir))
                return dir;
        }

        return null;
    }

    // ---- macOS statfs --------------------------------------------------------------------

    /// <summary>MNT_DONTBROWSE from sys/mount.h: the file system is not shown in Finder.</summary>
    private const uint MNT_DONTBROWSE = 0x00100000;

    /// <summary>
    /// Offset of f_flags in the 64-bit-inode struct statfs: f_bsize, f_iosize (4 + 4),
    /// five 64-bit counters (40), f_fsid (8), f_owner (4), f_type (4).
    /// </summary>
    private const int StatfsFlagsOffset = 64;

    /// <summary>sizeof(struct statfs) is 2168; the buffer is rounded up generously.</summary>
    private const int StatfsBufferSize = 4096;

    // Intel Macs export the 64-bit-inode layout under a suffixed name; Apple silicon has only
    // that layout, under the plain one.
    [DllImport("libc", EntryPoint = "statfs$INODE64", SetLastError = true)]
    private static extern int StatfsX64(string path, byte[] buffer);

    [DllImport("libc", EntryPoint = "statfs", SetLastError = true)]
    private static extern int StatfsArm64(string path, byte[] buffer);

    private static uint? MacMountFlags(string path)
    {
        try
        {
            var buffer = new byte[StatfsBufferSize];
            var result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? StatfsX64(path, buffer)
                : StatfsArm64(path, buffer);

            return result == 0 ? BitConverter.ToUInt32(buffer, StatfsFlagsOffset) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
