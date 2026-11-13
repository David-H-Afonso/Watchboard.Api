using System.Text;
using Watchboard.Api.Contracts;
using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public sealed record VerifiedGame(string SteamAppId, string AllKeyShopUrl, string AllKeyShopSku, string Name)
{
    public string SteamUrl => $"https://store.steampowered.com/app/{SteamAppId}/";
}

public static class GameIdentities
{
    private static readonly IReadOnlyDictionary<string, string> RomanTitleNumbers = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["II"] = "2", ["III"] = "3", ["IV"] = "4", ["V"] = "5", ["VI"] = "6", ["VII"] = "7",
        ["VIII"] = "8", ["IX"] = "9", ["X"] = "10", ["XI"] = "11", ["XII"] = "12", ["XIII"] = "13",
        ["XIV"] = "14", ["XV"] = "15", ["XVI"] = "16", ["XVII"] = "17", ["XVIII"] = "18", ["XIX"] = "19", ["XX"] = "20"
    };
    // Independently checked public product identities. Packages are NOT app IDs:
    // Yakuza full 1063458 != upgrade 1470285; Mafia base 699541 != Definitive 1687573.
    public static IReadOnlyList<VerifiedGame> Verified { get; } = [
        new("2988580", "https://www.allkeyshop.com/blog/buy-yakuza-0-directors-cut-cd-key-compare-prices/", "198454", "Yakuza 0 Director's Cut"),
        new("1941540", "https://www.allkeyshop.com/blog/buy-mafia-the-old-country-cd-key-compare-prices/", "172551", "Mafia: The Old Country"),
        new("1388590", "https://www.allkeyshop.com/blog/buy-yakuza-6-the-song-of-life-cd-key-compare-prices/", "67648", "Yakuza 6: The Song of Life")
    ];

    public static VerifiedGame? Find(string sourceUrl)
    {
        var source = WatchValidation.GameUrl(sourceUrl);
        return source is null ? null : Verified.FirstOrDefault(x => source.Value.Provider == "steam"
            ? x.SteamAppId == source.Value.AppId : x.AllKeyShopUrl == source.Value.Url);
    }

    public static bool BaseEdition(string edition) => edition.Trim().Equals("Standard", StringComparison.OrdinalIgnoreCase);

    public static string IdentityTitle(GameResolution value) => value.Provider == "allkeyshop" && value.AllKeyShopEdition is { } edition &&
        !BaseEdition(edition) && !(value.ProductType == "dlc" && IsDlcEdition(edition)) &&
        !TitleKey(value.Name).EndsWith(TitleKey(edition), StringComparison.Ordinal)
            ? $"{value.Name} {edition}" : value.Name;

    public static string? MatchingEdition(GameResolution market, string steamTitle, string? steamProductType = null)
    {
        if (steamProductType is not null && market.ProductType is not null && steamProductType != market.ProductType) return null;
        var matches = market.Editions.Where(edition => SameTitle(IdentityTitle(market with { AllKeyShopEdition = edition }), steamTitle))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public static bool IsDlcEdition(string edition) => edition.Trim().ToUpperInvariant() is "DLC" or "DLC PACK" or "SEASON PASS" or "DLC SEASON PASS";

    public static string? VerifiedSteamAppId(GameWatch watch) => watch.Provider == "steam" ? watch.SteamAppId :
        watch.CounterpartStatus == "verified" && (BaseEdition(watch.Edition) ||
            watch.SteamAppId is not null && WatchValidation.GameUrl(watch.CounterpartUrl)?.AppId == watch.SteamAppId) ? watch.SteamAppId : null;

    public static IReadOnlyList<string> VariantComparisonCandidates(string title)
    {
        // A shorter URL is only a location candidate. The original FULL Steam title must subsequently
        // equal Product name + an explicitly published edition. Standard is never substituted for it.
        var suffix = new[] { "Infernal Edition", "Enhanced Edition", "Enhanced", "Deluxe Edition", "Deluxe", "Ultimate Edition", "Ultimate",
            "Definitive Edition", "Complete Edition", "Premium Edition", "Premium" }
            .FirstOrDefault(value => title.EndsWith(" " + value, StringComparison.OrdinalIgnoreCase));
        if (suffix is null) return [];
        var baseTitle = title[..^(suffix.Length + 1)];
        // A variant's market URL is often the base-product page with the chosen edition in its
        // published selector. Try the roman/numeric-normalized base slug first (e.g. Diablo II/2).
        return PublicComparisonCandidates(CanonicalTitle(baseTitle, abbreviateThousands: false)).Take(1).ToList();
    }

    // Positive public identity evidence checked on2026-11-10. This is a transport-only fallback,
    // NOT a replacement for generic discovery and never a price/stock/language cache. Healthy
    // responses still go through exact Product/app validation, and identity conflicts never fall back.
    private static IReadOnlyList<VerifiedGame> TransportEvidence { get; } = [
        new("3159330", "https://www.allkeyshop.com/blog/buy-assassins-creed-shadows-cd-key-compare-prices/", "165854", "Assassin's Creed Shadows"),
        new("756800", "https://www.allkeyshop.com/blog/buy-contraband-police-cd-key-compare-prices/", "55214", "Contraband Police"),
        new("4115450", "https://www.allkeyshop.com/blog/buy-phantom-blade-zero-cd-key-compare-prices/", "139675", "Phantom Blade Zero")
    ];

    public static GameResolution? TransportFallback(string sourceUrl, string? observedTitle = null, string? observedSku = null)
    {
        var source = WatchValidation.GameUrl(sourceUrl);
        if (source is null) return null;
        var known = TransportEvidence.FirstOrDefault(game => source.Value.Provider == "steam"
            ? game.SteamAppId == source.Value.AppId : game.AllKeyShopUrl == source.Value.Url);
        if (known is null || observedTitle is not null && !SameTitle(known.Name, observedTitle) || observedSku is not null && known.AllKeyShopSku != observedSku) return null;
        return new(source.Value.Provider, source.Value.Url, known.Name, known.SteamAppId,
            ["Standard"], known.SteamUrl, known.AllKeyShopUrl, "verified",
            "Identidad de producto comprobada con metadatos públicos el10/11/2026; la fuente no respondió ahora y no se ha inventado ningún precio.", ProductType: "game");
    }

    // Punctuation, common Roman/Arabic sequel notation and numeric K shorthand only.
    // All title words and edition qualifiers remain required; no fuzzy/substring comparison.
    public static string TitleKey(string title) => string.Concat(PresentationText(title).Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(NormalizeTitleToken));
    public static bool SameTitle(string first, string second) => TitleKey(first) == TitleKey(second);

    public static IReadOnlyList<string> PublicComparisonCandidates(string title)
    {
        var slugs = new[] { title, CanonicalTitle(title, abbreviateThousands: true), CanonicalTitle(title, abbreviateThousands: false) }
            .Select(Slug).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        // Full title, including numbers/edition/DLC words. Slugs propose pages; only fetched Product
        // title + PC category + SKU + canonical scope can establish a match.
        return slugs.SelectMany(slug => new[] { $"https://www.allkeyshop.com/blog/buy-{slug}-cd-key-compare-prices/",
                $"https://www.allkeyshop.com/blog/buy-{slug}-key-compare-prices/" })
            .Where(url => WatchValidation.GameUrl(url) is not null).Distinct(StringComparer.Ordinal).Take(3).ToList();
    }

    private static string PresentationText(string value) => new(value.Where(character => character is not
        ('™' or '®' or '℠' or 'Ⓡ' or '\'' or '’' or '‘' or '‛' or 'ʼ' or 'ʻ' or '＇' or '′' or '`' or '´')).ToArray());

    private static string CanonicalTitle(string title, bool abbreviateThousands) => string.Join(' ', title.Normalize(NormalizationForm.FormKC)
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(token =>
        {
            var clean = string.Concat(token.Where(char.IsLetterOrDigit)).ToUpperInvariant();
            if (RomanTitleNumbers.TryGetValue(clean, out var number)) return number;
            if (clean.Length > 1 && clean[^1] == 'K' && clean[..^1].All(char.IsAsciiDigit))
                return abbreviateThousands ? clean : clean[..^1] + "000";
            if (abbreviateThousands && clean.Length is >= 4 and <= 6 && clean.All(char.IsAsciiDigit) && clean.EndsWith("000", StringComparison.Ordinal))
                return clean[..^3] + "K";
            return clean;
        }));

    private static string NormalizeTitleToken(string token)
    {
        var clean = string.Concat(token.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        if (RomanTitleNumbers.TryGetValue(clean, out var number)) return number;
        return clean.Length > 1 && clean[^1] == 'K' && clean[..^1].All(char.IsAsciiDigit) ? clean[..^1] + "000" : clean;
    }

    private static string Slug(string value)
    {
        var result = new StringBuilder();
        foreach (var character in PresentationText(value).Normalize(NormalizationForm.FormKD).ToLowerInvariant())
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsAsciiLetterOrDigit(character)) result.Append(character);
            else if (result.Length > 0 && result[^1] != '-') result.Append('-');
        }
        return result.ToString().Trim('-');
    }

    public static GameResolution? KnownResolution(string sourceUrl)
    {
        var known = Find(sourceUrl);
        var source = WatchValidation.GameUrl(sourceUrl);
        return known is null || source is null ? null : new(source.Value.Provider, source.Value.Url, known.Name, known.SteamAppId,
            ["Standard"], known.SteamUrl, known.AllKeyShopUrl, "verified",
            "Identidad base verificada entre las páginas públicas de Steam y AllKeyShop; objetivos e historial independientes.", ProductType: "game");
    }

    public static GameResolution Unverified(string provider, string url, string name, IReadOnlyList<string> editions, string? candidateId = null) =>
        new(provider, url, name, provider == "steam" ? WatchValidation.GameUrl(url)?.AppId : null, editions,
            provider == "steam" ? url : candidateId is null ? null : $"https://store.steampowered.com/app/{candidateId}/",
            provider == "allkeyshop" ? url : null, "needs_review",
            "No hay una coincidencia pública exacta verificada. Puedes indicar el enlace de la otra tienda; no se eliminan calificadores ni se adivina la identidad desde un slug.");
}
