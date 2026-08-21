using System.Text.Json;
using System.Text.Json.Serialization;
using SparkPhotoDownloader.Models;

namespace SparkPhotoDownloader.Services;

/// <summary>Where the program keeps its database, thumbnail cache and settings.</summary>
public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SparkPhotoDownloader");

    public static string SettingsFile => Path.Combine(DataDirectory, "settings.json");

    public static string HistoryDatabase => Path.Combine(DataDirectory, "download-history.sqlite");

    public static string ThumbnailCacheDirectory => Path.Combine(DataDirectory, "thumbnails");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(ThumbnailCacheDirectory);
    }

    /// <summary>The user's Pictures folder, used as the initial photo destination.</summary>
    public static string DefaultPhotoDestination =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

    /// <summary>The user's Videos folder, used as the initial video destination.</summary>
    public static string DefaultVideoDestination =>
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON, debouncing rapid writes.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _gate = new();
    private DateTime _lastSave = DateTime.MinValue;

    public AppSettings Settings { get; private set; } = new();

    public SettingsService() => Load();

    public void Load()
    {
        AppPaths.EnsureCreated();

        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                Settings = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch (Exception)
        {
            // A corrupt settings file must never stop the program starting.
            Settings = new AppSettings();
        }

        Settings.PhotoDestination ??= AppPaths.DefaultPhotoDestination;
        Settings.VideoDestination ??= AppPaths.DefaultVideoDestination;

        // "Downloads today" resets when the day rolls over.
        if (Settings.DownloadsTodayDate.Date != DateTime.Today)
        {
            Settings.DownloadsTodayDate = DateTime.Today;
            Settings.DownloadsToday = 0;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                AppPaths.EnsureCreated();
                var json = JsonSerializer.Serialize(Settings, Options);
                var temp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.SettingsFile, overwrite: true);
                _lastSave = DateTime.UtcNow;
            }
            catch (Exception)
            {
                // Losing a preference is not worth interrupting the user over.
            }
        }
    }

    /// <summary>Save at most once a second, for properties that change as the user drags a slider.</summary>
    public void SaveThrottled()
    {
        if (DateTime.UtcNow - _lastSave < TimeSpan.FromSeconds(1))
            return;
        Save();
    }

    // ---- Preset access -------------------------------------------------------------------

    public List<NamePreset> PhotoSubfolderPresets() =>
        [.. BuiltInPresets.PhotoSubfolders(), .. Settings.CustomPhotoSubfolderPresets];

    public List<NamePreset> VideoSubfolderPresets() =>
        [.. BuiltInPresets.VideoSubfolders(), .. Settings.CustomVideoSubfolderPresets];

    public List<NamePreset> PhotoRenamePresets() =>
        [.. BuiltInPresets.PhotoRenames(), .. Settings.CustomPhotoRenamePresets];

    public List<NamePreset> VideoRenamePresets() =>
        [.. BuiltInPresets.VideoRenames(), .. Settings.CustomVideoRenamePresets];

    public static NamePreset? Find(IEnumerable<NamePreset> presets, string? name) =>
        presets.FirstOrDefault(p => p.Name == name);
}
