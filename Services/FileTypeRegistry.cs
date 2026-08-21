using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.Services;

/// <summary>
/// Knows which extensions count as photos, videos, audio, or sidecars, and which of those
/// are raw formats needing an embedded preview rather than a direct decode.
/// </summary>
public static class FileTypeRegistry
{
    /// <summary>Formats a normal image decoder can open directly.</summary>
    private static readonly HashSet<string> RenderablePhotos = new(StringComparer.OrdinalIgnoreCase)
    {
        "jpg", "jpeg", "jpe", "jfif", "png", "bmp", "gif", "webp", "tif", "tiff",
    };

    /// <summary>Raw formats: the thumbnail has to come out of the embedded preview.</summary>
    private static readonly HashSet<string> RawPhotos = new(StringComparer.OrdinalIgnoreCase)
    {
        "arw", "srf", "sr2",            // Sony
        "cr2", "cr3", "crw",            // Canon
        "nef", "nrw",                   // Nikon
        "orf",                          // Olympus / OM System
        "raf",                          // Fujifilm
        "rw2", "raw",                   // Panasonic / Leica
        "pef", "ptx",                   // Pentax
        "dng",                          // Adobe / Leica / Pixel
        "srw",                          // Samsung
        "x3f",                          // Sigma
        "3fr", "fff",                   // Hasselblad
        "iiq",                          // Phase One
        "mrw",                          // Minolta
        "erf",                          // Epson
        "mef",                          // Mamiya
        "rwl",                          // Leica
        "dcr", "kdc",                   // Kodak
        "mos",                          // Leaf
        "gpr",                          // GoPro
        "heic", "heif", "hif", "avif",  // modern container formats
    };

    private static readonly HashSet<string> Videos = new(StringComparer.OrdinalIgnoreCase)
    {
        "mp4", "m4v", "mov", "qt", "avi", "mts", "m2ts", "mt2s", "ts", "mpg", "mpeg",
        "mkv", "wmv", "asf", "3gp", "3g2", "flv", "webm", "mxf", "insv", "lrv", "braw", "r3d",
    };

    private static readonly HashSet<string> Audio = new(StringComparer.OrdinalIgnoreCase)
    {
        "wav", "mp3", "m4a", "flac", "aac", "ogg",
    };

    /// <summary>Files that ride along with a photo or video rather than downloading on their own.</summary>
    private static readonly HashSet<string> Sidecars = new(StringComparer.OrdinalIgnoreCase)
    {
        "xmp", "thm", "pp3", "aae", "dop", "on1", "acr",
    };

    /// <summary>Folder names that mark a volume as camera media.</summary>
    public static readonly string[] CameraFolderNames =
        ["DCIM", "PRIVATE", "MP_ROOT", "AVCHD", "CLIP", "PANA_GRP", "MISC", "M4ROOT", "XDROOT"];

    public static string NormalizeExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Length > 1 ? ext[1..].ToLowerInvariant() : string.Empty;
    }

    public static FileKind Classify(string extension)
    {
        if (RenderablePhotos.Contains(extension) || RawPhotos.Contains(extension))
            return FileKind.Photo;
        if (Videos.Contains(extension))
            return FileKind.Video;
        if (Audio.Contains(extension))
            return FileKind.Audio;
        if (Sidecars.Contains(extension))
            return FileKind.Sidecar;
        return FileKind.Unknown;
    }

    public static bool IsRaw(string extension) => RawPhotos.Contains(extension);

    public static bool IsDirectlyDecodable(string extension) => RenderablePhotos.Contains(extension);

    public static bool IsSidecar(string extension) => Sidecars.Contains(extension);

    /// <summary>True for the kinds the program actually downloads.</summary>
    public static bool IsDownloadable(FileKind kind) => kind is FileKind.Photo or FileKind.Video;
}
