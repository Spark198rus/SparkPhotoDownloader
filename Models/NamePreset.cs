using System.Text.Json.Serialization;

namespace SparkPhotoDownloader.Models;

/// <summary>One substitution in a generated folder path or file name.</summary>
public sealed class NamePart
{
    public NameToken Token { get; set; }

    /// <summary>Literal text, for <see cref="NameToken.Text"/>.</summary>
    public string? Text { get; set; }

    /// <summary>Date format key (see <c>DateFormats</c>) for <see cref="NameToken.Date"/>.</summary>
    public string? Format { get; set; }

    public DateSource DateSource { get; set; } = DateSource.CaptureDate;

    public TextCase Case { get; set; } = TextCase.Original;

    /// <summary>Zero padding for sequence tokens, or digit count for image numbers.</summary>
    public int Digits { get; set; } = 1;

    public NamePart Clone() => new()
    {
        Token = Token,
        Text = Text,
        Format = Format,
        DateSource = DateSource,
        Case = Case,
        Digits = Digits,
    };

    public static NamePart Literal(string text) => new() { Token = NameToken.Text, Text = text };

    public static NamePart Sep() => new() { Token = NameToken.Separator };

    public static NamePart Date(string format, DateSource source = DateSource.CaptureDate) =>
        new() { Token = NameToken.Date, Format = format, DateSource = source };

    public static NamePart Of(NameToken token, int digits = 1) => new() { Token = token, Digits = digits };
}

/// <summary>A named, reusable sequence of <see cref="NamePart"/>s.</summary>
public sealed class NamePreset
{
    public required string Name { get; set; }

    public List<NamePart> Parts { get; set; } = [];

    /// <summary>Built-in presets ship with the program and cannot be deleted.</summary>
    public bool IsBuiltIn { get; set; }

    [JsonIgnore]
    public bool IsCustom => !IsBuiltIn;

    public NamePreset Clone() => new()
    {
        Name = Name,
        IsBuiltIn = false,
        Parts = Parts.Select(p => p.Clone()).ToList(),
    };

    public override string ToString() => Name;
}
