using System.Text.Json;
using Watchboard.Api.Contracts;

namespace Watchboard.Api.Services;

public sealed record OfferContext(string ActivationPlatform, string DeliveryType, string Region, string? LanguageRestriction);

public static class QuoteContext
{
    public static string? Platform(string? value) => Label(value) switch
    {
        "STEAM" => "steam",
        "UBISOFT-CONNECT" or "UBISOFT CONNECT" or "UBISOFT" or "UPLAY" => "ubisoft-connect",
        "EPIC" or "EPIC-STORE" or "EPIC STORE" or "EPIC GAMES" or "EPIC GAMES STORE" or "EPIC-GAMES" or "EPIC-GAMES-STORE" => "epic",
        "GOG" or "GOG.COM" => "gog",
        "EA-APP" or "EA APP" or "ORIGIN" or "EA-ORIGIN" => "ea-app",
        "ROCKSTAR" or "ROCKSTAR GAMES" or "ROCKSTAR GAMES LAUNCHER" or "ROCKSTAR-SOCIAL-CLUB" or "ROCKSTAR SOCIAL CLUB" => "rockstar",
        "BATTLENET" or "BATTLE.NET" or "BATTLE NET" => "battlenet",
        "MICROSOFT-STORE" or "MICROSOFT STORE" or "MICROSOFT-WINDOWS" or "MICROSOFT WINDOWS" => "microsoft-store",
        _ => null // In particular, Xbox/console/ROW/wallet/unknown is not an implicit PC substitute.
    };

    public static OfferContext? Classify(JsonElement offer, JsonElement region)
    {
        var platform = Platform(Text(offer, "activationPlatform"));
        if (platform is null) return null;
        var filter = Label(Text(region, "filter_name"));
        var name = Label(Text(region, "region_name"));
        if (filter.Length == 0 || name.Length == 0 || BadClassification(filter) || BadClassification(name)) return null;
        var prefixes = platform switch
        {
            "steam" => new[] { "STEAM" },
            "ubisoft-connect" => ["UBISOFT CONNECT", "UBISOFT", "UPLAY"],
            "epic" => ["EPIC GAMES STORE", "EPIC GAMES", "EPIC STORE", "EPIC"],
            "gog" => ["GOG.COM", "GOG"],
            "ea-app" => ["EA APP", "ORIGIN", "EA"],
            "rockstar" => ["ROCKSTAR GAMES", "ROCKSTAR"],
            "battlenet" => ["BATTLE.NET", "BATTLE NET", "BATTLENET"],
            _ => ["MICROSOFT STORE", "MICROSOFT WINDOWS", "WINDOWS"]
        };
        var prefix = prefixes.FirstOrDefault(value => filter.StartsWith(value + " ", StringComparison.Ordinal));
        var suffix = prefix is null ? filter : filter[(prefix.Length + 1)..];
        var delivery = suffix.Contains("GIFT", StringComparison.Ordinal) ? "gift" : "key";
        var language = suffix == "GIFT EN LANG." ? "en" : null;
        var geography = suffix switch
        {
            "GLOBAL" or "GIFT GLOBAL" or "GLOBAL GIFT" => "GLOBAL",
            "EU" or "EUROPE" or "GIFT EU" or "GIFT EUROPE" or "EU GIFT" or "EUROPE GIFT" => "EUROPE",
            "SPAIN" or "ES" or "GIFT SPAIN" or "SPAIN GIFT" => "SPAIN",
            "EU/US" when prefix is null && platform == "steam" => "EU/US",
            "GIFT" when prefix is not null => Geography(name),
            "GIFT EN LANG." when platform == "steam" => EnglishGiftGeography(region),
            _ => null
        };
        if (geography is null || prefix is null && suffix != "EU/US") return null;
        var namedGeography = Geography(name);
        if (namedGeography is not null && namedGeography != geography && !(geography == "EU/US" && namedGeography == "EUROPE")) return null;
        if (namedGeography is null && name is not ("GIFT" or "GIFT EN LANG." or "KEY")) return null;

        bool? giftFlag = null;
        foreach (var key in new[] { "gift", "isGift" })
        {
            if (!offer.TryGetProperty(key, out var value)) continue;
            if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var flag = value.GetBoolean();
            if (giftFlag.HasValue && giftFlag != flag) return null;
            giftFlag = flag;
        }
        if (delivery == "gift" && giftFlag == false) return null;
        if (giftFlag == true) delivery = "gift";
        if (offer.TryGetProperty("deliveryType", out var declared))
        {
            if (declared.ValueKind != JsonValueKind.String || declared.GetString() is not ("key" or "gift") || declared.GetString() != delivery) return null;
        }
        if (offer.TryGetProperty("languageRestriction", out var restriction) && restriction.ValueKind != JsonValueKind.Null)
        {
            if (restriction.ValueKind != JsonValueKind.String || Label(restriction.GetString()) is not ("EN" or "ENGLISH")) return null;
            language = "en";
        }
        return new(platform, delivery, geography, language);
    }

    public static bool CompatibleRegion(string watchRegion, string region) => region == "GLOBAL" ||
        watchRegion == "eu-global" && region is "EUROPE" or "SPAIN" or "EU/US";

    public static bool Comparable(PricePoint first, PricePoint second) => first.ActivationPlatform is not null &&
        first.DeliveryType is "key" or "gift" or "direct" && first.ActivationPlatform == second.ActivationPlatform &&
        first.DeliveryType == second.DeliveryType && first.LanguageRestriction == second.LanguageRestriction &&
        first.Currency == second.Currency && first.Region == second.Region &&
        first.Edition.Trim().Equals(second.Edition.Trim(), StringComparison.OrdinalIgnoreCase);

    public static string PlatformName(string? platform) => platform switch
    {
        "steam" => "Steam", "ubisoft-connect" => "Ubisoft Connect", "epic" => "Epic Games", "gog" => "GOG",
        "ea-app" => "EA app", "rockstar" => "Rockstar", "battlenet" => "Battle.net", "microsoft-store" => "Microsoft Store",
        _ => "Plataforma sin confirmar"
    };

    private static string? EnglishGiftGeography(JsonElement region)
    {
        foreach (var key in new[] { "scope", "geography", "activationRegion", "region_name" })
            if (Geography(Label(Text(region, key))) is { } known) return known;
        // A language label is NOT geography proof. Only unambiguous structured descriptions qualify;
        // free-form instructions mentioning login/accounts are never used as account classification.
        foreach (var key in new[] { "description", "region_description" })
            if (Label(Text(region, key)) is "GLOBAL" or "WORLDWIDE" or "CAN BE ACTIVATED WORLDWIDE" or "CAN BE ACTIVATED WORLDWIDE.")
                return "GLOBAL";
        return null;
    }

    private static string? Geography(string value) => value switch
    {
        "GLOBAL" or "WORLDWIDE" => "GLOBAL",
        "EU" or "EUROPE" or "GIFT EU" or "GIFT EUROPE" => "EUROPE",
        "SPAIN" or "ESPAÑA" or "ES" or "GIFT SPAIN" => "SPAIN",
        "EU/US" => "EU/US",
        _ => null
    };

    private static bool BadClassification(string value) => value.Contains("ACCOUNT", StringComparison.Ordinal) ||
        value.Contains("GIFT CARD", StringComparison.Ordinal) || value.Contains("GIFTCARD", StringComparison.Ordinal) ||
        value.Contains("WALLET", StringComparison.Ordinal) || value.Contains("CREDIT", StringComparison.Ordinal);
    private static string Label(string? value) => value is null ? "" : string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    private static string? Text(JsonElement element, string key) => element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
