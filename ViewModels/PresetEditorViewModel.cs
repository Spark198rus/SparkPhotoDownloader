using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SparkPhotoDownloader.Models;
using SparkPhotoDownloader.Services;

namespace SparkPhotoDownloader.ViewModels;

/// <summary>One row in the preset editor's parts list.</summary>
public sealed partial class NamePartViewModel : ViewModelBase
{
    private readonly Action _onChanged;

    public NamePartViewModel(NamePart part, Action onChanged)
    {
        Part = part;
        _onChanged = onChanged;

        Token = part.Token;
        Text = part.Text ?? string.Empty;
        Format = part.Format ?? DateFormats.Default;
        DateSource = part.DateSource;
        Case = part.Case;
        Digits = part.Digits;
    }

    public NamePart Part { get; }

    public IReadOnlyList<NameToken> TokenOptions { get; } = TokenLabels.All.ToList();

    public IReadOnlyList<string> FormatOptions { get; } = DateFormats.All.Keys.ToList();

    public IReadOnlyList<DateSource> DateSourceOptions { get; } = Enum.GetValues<DateSource>();

    public IReadOnlyList<TextCase> CaseOptions { get; } = Enum.GetValues<TextCase>();

    public IReadOnlyList<int> DigitOptions { get; } = [1, 2, 3, 4, 5, 6];

    [NotifyPropertyChangedFor(nameof(IsText))]
    [NotifyPropertyChangedFor(nameof(IsDate))]
    [NotifyPropertyChangedFor(nameof(HasDigits))]
    [NotifyPropertyChangedFor(nameof(HasCase))]
    [ObservableProperty]
    public partial NameToken Token { get; set; }

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Format { get; set; } = DateFormats.Default;

    [ObservableProperty]
    public partial DateSource DateSource { get; set; }

    [ObservableProperty]
    public partial TextCase Case { get; set; }

    [ObservableProperty]
    public partial int Digits { get; set; } = 1;

    public bool IsText => Token == NameToken.Text;

    public bool IsDate => Token == NameToken.Date;

    public bool HasDigits => Token is NameToken.DownloadsToday or NameToken.SessionSequence
        or NameToken.StoredSequence or NameToken.SubfolderSequence or NameToken.ImageNumber;

    public bool HasCase => Token is not (NameToken.Separator or NameToken.Text);

    /// <summary>Copy the editable values back onto the model this row wraps.</summary>
    public NamePart ToPart() => new()
    {
        Token = Token,
        Text = Text,
        Format = Format,
        DateSource = DateSource,
        Case = Case,
        Digits = Digits,
    };

    partial void OnTokenChanged(NameToken value) => _onChanged();

    partial void OnTextChanged(string value) => _onChanged();

    partial void OnFormatChanged(string value) => _onChanged();

    partial void OnDateSourceChanged(DateSource value) => _onChanged();

    partial void OnCaseChanged(TextCase value) => _onChanged();

    partial void OnDigitsChanged(int value) => _onChanged();
}

/// <summary>
/// The naming preset editor. Built-in presets are read-only templates: editing one starts a
/// copy, which is how a user ends up with their own scheme without losing the originals.
/// </summary>
public sealed partial class PresetEditorViewModel : ViewModelBase
{
    private readonly SettingsService _settings;
    private readonly IUiServices _ui;
    private readonly FileKind _kind;
    private readonly bool _subfolders;

    public PresetEditorViewModel(
        SettingsService settings, FileKind kind, bool subfolders, IUiServices ui)
    {
        _settings = settings;
        _kind = kind;
        _subfolders = subfolders;
        _ui = ui;

        Title = $"{(subfolders ? "Subfolder" : "File name")} presets — " +
                (kind == FileKind.Video ? "videos" : "photos");

        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault();
    }

    /// <summary>True when the user saved at least one change, so callers can reload.</summary>
    public bool Saved { get; private set; }

    public string Title { get; }

    public ObservableCollection<NamePreset> Presets { get; } = [];

    public ObservableCollection<NamePartViewModel> Parts { get; } = [];

    public IReadOnlyList<NameToken> TokenOptions { get; } = TokenLabels.All.ToList();

    [NotifyPropertyChangedFor(nameof(CanEditSelected))]
    [NotifyPropertyChangedFor(nameof(SelectedIsBuiltIn))]
    [ObservableProperty]
    public partial NamePreset? SelectedPreset { get; set; }

    [ObservableProperty]
    public partial NamePartViewModel? SelectedPart { get; set; }

    [ObservableProperty]
    public partial string Preview { get; set; } = string.Empty;

    [ObservableProperty]
    public partial NameToken TokenToAdd { get; set; } = NameToken.Text;

    public bool SelectedIsBuiltIn => SelectedPreset?.IsBuiltIn ?? false;

    public bool CanEditSelected => SelectedPreset is { IsBuiltIn: false };

    public string EditHint => SelectedIsBuiltIn
        ? "This is a built-in preset. Use \"Duplicate\" to make an editable copy."
        : "Add, remove and reorder the parts below. Changes are saved as you make them.";

    private void ReloadPresets()
    {
        var previous = SelectedPreset?.Name;

        Presets.Clear();

        var list = (_kind, _subfolders) switch
        {
            (FileKind.Video, true) => _settings.VideoSubfolderPresets(),
            (FileKind.Video, false) => _settings.VideoRenamePresets(),
            (_, true) => _settings.PhotoSubfolderPresets(),
            _ => _settings.PhotoRenamePresets(),
        };

        foreach (var preset in list)
            Presets.Add(preset);

        if (previous is not null)
            SelectedPreset = Presets.FirstOrDefault(p => p.Name == previous) ?? Presets.FirstOrDefault();
    }

    private List<NamePreset> CustomList() => (_kind, _subfolders) switch
    {
        (FileKind.Video, true) => _settings.Settings.CustomVideoSubfolderPresets,
        (FileKind.Video, false) => _settings.Settings.CustomVideoRenamePresets,
        (_, true) => _settings.Settings.CustomPhotoSubfolderPresets,
        _ => _settings.Settings.CustomPhotoRenamePresets,
    };

    partial void OnSelectedPresetChanged(NamePreset? value)
    {
        Parts.Clear();

        if (value is not null)
        {
            foreach (var part in value.Parts)
                Parts.Add(new NamePartViewModel(part, OnPartEdited));
        }

        OnPropertyChanged(nameof(EditHint));
        UpdatePreview();
    }

    private void OnPartEdited()
    {
        if (SelectedPreset is not { IsBuiltIn: false } preset)
        {
            UpdatePreview();
            return;
        }

        preset.Parts = Parts.Select(p => p.ToPart()).ToList();
        Persist();
        UpdatePreview();
    }

    private void Persist()
    {
        _settings.Save();
        Saved = true;
    }

    // ---- Preview -------------------------------------------------------------------------

    /// <summary>
    /// A stand-in file used to render the preview, so the editor shows a realistic result
    /// without needing a card in the reader.
    /// </summary>
    private static readonly SourceFile SampleFile = new()
    {
        FullPath = @"X:\DCIM\100CANON\IMG_0947.CR3",
        FileName = "IMG_0947.CR3",
        Extension = "cr3",
        Size = 28_400_000,
        ModifiedUtc = new DateTime(2026, 3, 14, 9, 27, 51, DateTimeKind.Utc),
        Kind = FileKind.Photo,
        SourceId = "sample",
        SourceName = "Memory card",
        RelativeFolder = "DCIM/100CANON",
    };

    private static readonly FileMetadata SampleMetadata = new()
    {
        CaptureDate = new DateTime(2026, 3, 14, 9, 27, 51),
        CameraMake = "Canon",
        CameraModel = "Canon EOS R6 Mark II",
        SerialNumber = "042051000937",
        LensModel = "RF24-105mm F4 L IS USM",
        Aperture = 4.0,
        Iso = 400,
        ExposureTime = "1_250",
        FocalLength = 85,
        Width = 6000,
        Height = 4000,
    };

    private void UpdatePreview()
    {
        var parts = Parts.Count > 0
            ? Parts.Select(p => p.ToPart()).ToList()
            : SelectedPreset?.Parts ?? [];

        var preset = new NamePreset { Name = "preview", Parts = parts };

        var context = new NameContext
        {
            File = SampleFile,
            Metadata = SampleMetadata,
            JobCode = _settings.Settings.LastJobCode ?? "Job Code",
            Sequences = new SequenceState { DownloadsToday = 7, SessionSequence = 3, StoredSequence = 42 },
        };

        Preview = _subfolders
            ? NameGenerator.GenerateSubfolder(preset, context) is { Length: > 0 } folder
                ? folder.Replace(Path.DirectorySeparatorChar, '/')
                : "(destination root)"
            : NameGenerator.GenerateFileName(preset, context);
    }

    // ---- Commands ------------------------------------------------------------------------

    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (SelectedPreset is null)
            return;

        var name = await _ui.PromptAsync(
            "Duplicate preset", "Name for the new preset:", SelectedPreset.Name + " (copy)");

        if (string.IsNullOrWhiteSpace(name))
            return;

        var copy = SelectedPreset.Clone();
        copy.Name = MakeUniqueName(name);

        CustomList().Add(copy);
        Persist();
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == copy.Name);
    }

    [RelayCommand]
    private async Task CreateAsync()
    {
        var name = await _ui.PromptAsync("New preset", "Name for the new preset:", "My scheme");
        if (string.IsNullOrWhiteSpace(name))
            return;

        var preset = new NamePreset
        {
            Name = MakeUniqueName(name),
            Parts = _subfolders
                ? [NamePart.Date("YYYY"), NamePart.Sep(), NamePart.Date("YYYYMMDD")]
                : [NamePart.Of(NameToken.OriginalName)],
        };

        CustomList().Add(preset);
        Persist();
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name);
    }

    [RelayCommand]
    private async Task RenameAsync()
    {
        if (SelectedPreset is not { IsBuiltIn: false } preset)
            return;

        var name = await _ui.PromptAsync("Rename preset", "New name:", preset.Name);
        if (string.IsNullOrWhiteSpace(name))
            return;

        preset.Name = MakeUniqueName(name);
        Persist();
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault(p => p.Name == preset.Name);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedPreset is not { IsBuiltIn: false } preset)
            return;

        var confirmed = await _ui.ConfirmAsync(
            "Delete preset", $"Delete the preset \"{preset.Name}\"?", "Delete");

        if (!confirmed)
            return;

        CustomList().RemoveAll(p => p.Name == preset.Name);
        Persist();
        ReloadPresets();
        SelectedPreset = Presets.FirstOrDefault();
    }

    [RelayCommand]
    private void AddPart()
    {
        if (SelectedPreset is not { IsBuiltIn: false })
            return;

        var part = TokenToAdd == NameToken.Date
            ? NamePart.Date(DateFormats.Default)
            : new NamePart { Token = TokenToAdd, Text = TokenToAdd == NameToken.Text ? "-" : null };

        Parts.Add(new NamePartViewModel(part, OnPartEdited));
        OnPartEdited();
    }

    [RelayCommand]
    private void RemovePart(NamePartViewModel? part)
    {
        if (part is null || SelectedPreset is not { IsBuiltIn: false })
            return;

        Parts.Remove(part);
        OnPartEdited();
    }

    [RelayCommand]
    private void MovePartUp(NamePartViewModel? part)
    {
        if (part is null || SelectedPreset is not { IsBuiltIn: false })
            return;

        var index = Parts.IndexOf(part);
        if (index > 0)
        {
            Parts.Move(index, index - 1);
            OnPartEdited();
        }
    }

    [RelayCommand]
    private void MovePartDown(NamePartViewModel? part)
    {
        if (part is null || SelectedPreset is not { IsBuiltIn: false })
            return;

        var index = Parts.IndexOf(part);
        if (index >= 0 && index < Parts.Count - 1)
        {
            Parts.Move(index, index + 1);
            OnPartEdited();
        }
    }

    private string MakeUniqueName(string name)
    {
        name = name.Trim();
        if (Presets.All(p => p.Name != name))
            return name;

        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{name} {n}";
            if (Presets.All(p => p.Name != candidate))
                return candidate;
        }

        return name + " " + Guid.NewGuid().ToString("N")[..4];
    }
}
