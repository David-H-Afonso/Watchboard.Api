using System.Text.Json.Serialization;

namespace Watchboard.Api.Contracts;

public sealed record CreateSoftware(string Name, string Group, string Provider, string Source, string? InstalledVersion, Guid? LocationId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateSoftware(string Name, string Group, string Provider, string Source,
    [property: JsonRequired] string? InstalledVersion, [property: JsonRequired] string? InstalledOn,
    [property: JsonRequired] Guid? LocationId);
public sealed record SetInstalledVersion(string? InstalledVersion, string? InstalledOn, Guid? LocationId = null, bool UpdateLocation = false);
public sealed record LocationDto(Guid Id, string Name);
public sealed record CreateLocation(string Name);
public sealed record SetSoftwareLocation(Guid? LocationId);
public sealed record CreateGame(string Name, string SourceUrl, string Edition = "Standard", string Region = "eu-global", string Payment = "best",
    bool OfficialOnly = false, int? TargetCents = null, bool? IncludeSteam = null, bool? IncludeCounterpart = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UpdateGame(string Name, string SourceUrl, string Edition, string Region, string Payment,
    [property: JsonRequired] bool OfficialOnly, [property: JsonRequired] int? TargetCents);
public sealed record LinkCounterpart(string? SourceUrl = null);
public sealed record GameCreationDto(Guid Id, IReadOnlyList<Guid> RelatedIds, string MatchStatus, string? MatchNote);
public sealed record SetPriceTarget(int? TargetCents);
public sealed record PricePoint(long Id, DateTimeOffset ObservedAt, int PriceCents, string Currency, string Merchant,
    string Region, string Edition, string OfferUrl, string? Coupon, int MatchingOffers,
    int? OriginalPriceCents = null, int? DiscountPercent = null, DateTimeOffset? SaleEndsAt = null,
    string? PaymentMethod = null, int? BasePriceCents = null, int? FeeCents = null, string FeeBasis = "unknown",
    string? ActivationPlatform = null, string DeliveryType = "unknown", string? LanguageRestriction = null);
public sealed record SoftwareDto(Guid Id, string Name, string Group, string Provider, string Source, string SourceUrl,
    string? LocalTool, string? InstalledVersion, string? InstalledOn, string? InstalledMethod, DateTimeOffset? InstalledCheckedAt,
    string? DetectionError, string? LatestVersion, string? ReleaseUrl, string Comparison,
    DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt, string? Error, bool Stale,
    bool IncludeInWidget, DateTimeOffset? ReleasePublishedAt, Guid? LocationId = null, string? LocationName = null);
public sealed record GameDto(Guid Id, string Name, string Provider, string SourceUrl, string Edition, string Region,
    string Payment, bool OfficialOnly, int? TargetCents, DateTimeOffset? LastAttemptAt, DateTimeOffset? LastSuccessAt,
    string? Error, bool Stale, bool TargetReached, PricePoint? Current, int? LowestCents, int? ChangeCents, IReadOnlyList<PricePoint> History,
    bool IncludeInWidget, string? SteamAppId, IReadOnlyList<SalePeriod> SalePeriods, DateTimeOffset? LastTargetReachedAt,
    string CounterpartStatus, string? CounterpartNote, DateTimeOffset? LowestObservedAt, HistoryCoverage HistoryCoverage,
    int? TwoYearLowCents, DateTimeOffset? TwoYearLowObservedAt,
    string? LowestActivationPlatform = null, string? LowestDeliveryType = null,
    string? TwoYearLowActivationPlatform = null, string? TwoYearLowDeliveryType = null,
    string? LowestLanguageRestriction = null, string? TwoYearLowLanguageRestriction = null, string? CounterpartUrl = null);
public sealed record HistoryCoverage(DateTimeOffset RequestedFrom, DateTimeOffset RequestedTo, DateTimeOffset? OldestObservedAt,
    DateTimeOffset? NewestObservedAt, string Status, bool HistoricalBackfillAvailable, string Note);
public sealed record DashboardDto(DateTimeOffset GeneratedAt, string Host, string Platform, bool PollingEnabled,
    int PriceIntervalMinutes, int VersionIntervalMinutes, IReadOnlyList<SoftwareDto> Software, IReadOnlyList<GameDto> Games,
    UserDto User, WidgetPreferences Preferences, IReadOnlyList<LocationDto> Locations);
public sealed record ReleaseResult(string Version, string Url, DateTimeOffset? PublishedAt = null);
public sealed record PriceResult(int PriceCents, string Currency, string Merchant, string Region, string Edition,
    string OfferUrl, string? Coupon, int MatchingOffers, int? OriginalPriceCents = null, int? DiscountPercent = null,
    DateTimeOffset? SaleEndsAt = null, string? SteamAppId = null, string? PaymentMethod = null,
    int? BasePriceCents = null, int? FeeCents = null, string FeeBasis = "unknown",
    string? ActivationPlatform = null, string DeliveryType = "unknown", string? LanguageRestriction = null);
public sealed record RefreshResult(int Checked, int Succeeded, bool Busy, DateTimeOffset FinishedAt);
public sealed record RefreshRequest(bool Force = false, string Scope = "all");
public sealed record SalePeriod(DateTimeOffset FirstObservedAt, DateTimeOffset LastObservedAt, DateTimeOffset? EndedObservedAt,
    int LowestPriceCents, int? DiscountPercent);

public sealed record UserDto(Guid Id, string Username, string DisplayName, bool IsAdmin, bool Enabled);
public sealed record SessionDto(bool SetupRequired, UserDto? User);
public sealed record SetupRequest(string Password);
public sealed record LoginRequest(string Username, string Password);
public sealed record CreateUser(string Username, string DisplayName, string Password, bool IsAdmin);
public sealed record SetUserEnabled(bool Enabled);
public sealed record SetWidgetInclusion(bool Included);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WidgetPreferences(bool OnlyActionable = true, bool ShowVersions = true, bool ShowGames = true,
    bool ShowSales = false, bool ShowPriceDrops = false, bool ShowErrors = false, int MaxItems = 8,
    IReadOnlyList<Guid>? SoftwareLocationIds = null, bool SoftwareUpdatesOnly = true);
public sealed record WidgetItem(string Id, Guid WatchId, string Kind, string Title, string Value, string Detail,
    string SourceUrl, DateTimeOffset? ObservedAt);
public sealed record WidgetDto(DateTimeOffset GeneratedAt, UserDto User, IReadOnlyList<WidgetItem> Items);
public sealed record GameResolution(string Provider, string SourceUrl, string Name, string? SteamAppId, IReadOnlyList<string> Editions,
    string? SteamUrl = null, string? AllKeyShopUrl = null, string MatchStatus = "needs_review", string? MatchNote = null);
public sealed record ResolveGame(string SourceUrl);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfigDocument(string Format, int Version, PortableWidgetPreferences Preferences,
    IReadOnlyList<SoftwareConfig> Software, IReadOnlyList<GameConfig> Games,
    DateTimeOffset? ExportedAt = null, string? ExportedBy = null, IReadOnlyList<string>? Locations = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PortableWidgetPreferences(bool OnlyActionable = true, bool ShowVersions = true, bool ShowGames = true,
    bool ShowSales = false, bool ShowPriceDrops = false, bool ShowErrors = false, int MaxItems = 8,
    IReadOnlyList<string>? SoftwareLocations = null, bool SoftwareUpdatesOnly = true)
{
    // Convenient for legacy C# callers; export/import map location names explicitly, never serialize runtime IDs.
    public static implicit operator PortableWidgetPreferences(WidgetPreferences value)
    {
        if (value.SoftwareLocationIds is { Count: > 0 })
            throw new InvalidOperationException("Runtime location IDs require an explicit owner/name projection for portable configuration.");
        return new(value.OnlyActionable, value.ShowVersions, value.ShowGames, value.ShowSales, value.ShowPriceDrops,
            value.ShowErrors, value.MaxItems, value.SoftwareLocationIds is null ? null : [], value.SoftwareUpdatesOnly);
    }
}
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SoftwareConfig(string Name, string Group, string Provider, string Source, string? LocalTool,
    string? InstalledVersion, string? InstalledOn, bool IncludeInWidget, string? Location = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record GameConfig(string Name, string SourceUrl, string Edition, string Region, string Payment,
    bool OfficialOnly, int? TargetCents, bool IncludeInWidget, string? SteamAppId = null);
public sealed record ImportPreview(int SoftwareAdded, int SoftwareUpdated, int GamesAdded, int GamesUpdated, IReadOnlyList<string> Warnings);
