using System.Text;
using Watchboard.Api.Contracts;

namespace Watchboard.Api.Services;

public sealed record VerifiedGame(string SteamAppId, string AllKeyShopUrl, string AllKeyShopSku, string Name)
{
    public string SteamUrl => $"https://store.steampowered.com/app/{SteamAppId}/";
}

public static class GameIdentities
{
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
            "Identidad de producto comprobada con metadatos públicos el10/11/2026; la fuente no respondió ahora y no se ha inventado ningún precio.");
    }

    // Punctuation/case/Unicode presentation only. Never remove edition, upgrade, DLC, or title words.
    public static string TitleKey(string title) => string.Concat(PresentationText(title)
        .Normalize(NormalizationForm.FormKC).Where(char.IsLetterOrDigit)).ToUpperInvariant();
    public static bool SameTitle(string first, string second) => TitleKey(first) == TitleKey(second);

    public static IReadOnlyList<string> PublicComparisonCandidates(string title)
    {
        var plain = PresentationText(title);
        var slugs = new[] { plain, plain.Replace(".", "", StringComparison.Ordinal) }
            .Select(value =>
            {
                var result = new StringBuilder();
                foreach (var character in value.Normalize(NormalizationForm.FormKD).ToLowerInvariant())
                {
                    if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
                    if (char.IsAsciiLetterOrDigit(character)) result.Append(character);
                    else if (result.Length > 0 && result[^1] != '-') result.Append('-');
                }
                return result.ToString().Trim('-');
            }).Where(value => value.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        // Full title, including numbers/edition/DLC words. Slugs propose pages; only fetched Product
        // title + PC category + SKU + canonical scope can establish a match.
        return slugs.SelectMany(slug => new[] { $"https://www.allkeyshop.com/blog/buy-{slug}-cd-key-compare-prices/",
                $"https://www.allkeyshop.com/blog/buy-{slug}-key-compare-prices/" })
            .Where(url => WatchValidation.GameUrl(url) is not null).Distinct(StringComparer.Ordinal).Take(3).ToList();
    }

    private static string PresentationText(string value) => new(value.Where(character => character is not
        ('™' or '®' or '℠' or 'Ⓡ' or '\'' or '’' or '‘' or '‛' or 'ʼ' or 'ʻ' or '＇' or '′' or '`' or '´')).ToArray());

    public static GameResolution? KnownResolution(string sourceUrl)
    {
        var known = Find(sourceUrl);
        var source = WatchValidation.GameUrl(sourceUrl);
        return known is null || source is null ? null : new(source.Value.Provider, source.Value.Url, known.Name, known.SteamAppId,
            ["Standard"], known.SteamUrl, known.AllKeyShopUrl, "verified",
            "Identidad base verificada entre las páginas públicas de Steam y AllKeyShop; objetivos e historial independientes.");
    }

    public static GameResolution Unverified(string provider, string url, string name, IReadOnlyList<string> editions, string? candidateId = null) =>
        new(provider, url, name, provider == "steam" ? WatchValidation.GameUrl(url)?.AppId : null, editions,
            provider == "steam" ? url : candidateId is null ? null : $"https://store.steampowered.com/app/{candidateId}/",
            provider == "allkeyshop" ? url : null, "needs_review",
            "No hay una coincidencia pública exacta verificada. Puedes indicar el enlace de la otra tienda; no se eliminan calificadores ni se adivina la identidad desde un slug.");
}
