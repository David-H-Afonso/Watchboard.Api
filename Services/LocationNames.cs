using System.Text;
using System.Text.RegularExpressions;
using System.Globalization;

namespace Watchboard.Api.Services;

public static partial class LocationNames
{
    public static (string Name, string NormalizedName)? Canonicalize(string? value)
    {
        if (value is null || value.Length > 1000 || value.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c))) return null;
        string name;
        try { name = Whitespace().Replace(value.Normalize(NormalizationForm.FormKC).Trim(), " "); }
        catch (ArgumentException) { return null; }
        if (name.Length is < 1 or > 100) return null;
        var alias = Identity(name).Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);
        name = alias switch
        {
            "LINUX" or "GNU/LINUX" or "GNULINUX" => "Linux",
            "WINDOWS" or "WIN" => "Windows",
            "STEAMDECK" => "Steam Deck",
            "CASAOS" => "CasaOS",
            "MACOS" or "OSX" => "macOS",
            _ => name
        };
        return (name, Identity(name));
    }

    // Same case/diacritic identity as the editable selector; retain the first custom display spelling.
    private static string Identity(string value) => string.Concat(value.Normalize(NormalizationForm.FormD).EnumerateRunes()
        .Where(rune => Rune.GetUnicodeCategory(rune) is not (UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark))
        .Select(rune => rune.ToString())).ToUpperInvariant().Replace("ß", "SS", StringComparison.Ordinal).Replace("ẞ", "SS", StringComparison.Ordinal);

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();
}
