namespace Watchboard.Api.Models;

public static class DefaultAccount
{
    public static readonly Guid AdminId = Guid.Parse("00000000-0000-0000-0000-000000000001");
}

public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = "";
    public string NormalizedUsername { get; set; } = "";
    public string DisplayName { get; set; } = "";
    // Null only for the reserved, not-yet-configured first-run administrator.
    public string? PasswordHash { get; set; }
    public bool IsAdmin { get; set; }
    public bool Enabled { get; set; } = true;
    public string SessionStamp { get; set; } = Guid.NewGuid().ToString("N");
    public bool RequestedWatchesSeeded { get; set; }
    public bool OnlyActionable { get; set; } = true;
    public bool ShowVersions { get; set; } = true;
    public bool ShowGames { get; set; } = true;
    public bool ShowSales { get; set; }
    public bool ShowPriceDrops { get; set; }
    public bool ShowErrors { get; set; }
    public int MaxItems { get; set; } = 8;
    // JSON storage deliberately distinguishes null (all locations) from [] (none).
    public string? SoftwareLocationIdsJson { get; set; }
    public bool SoftwareUpdatesOnly { get; set; } = true;
}

public sealed class Location
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
}

public sealed class SoftwareWatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; } = DefaultAccount.AdminId;
    public bool IncludeInWidget { get; set; } = true;
    public string Name { get; set; } = "";
    public string Group { get; set; } = "pc";
    public string Provider { get; set; } = "github";
    public string Source { get; set; } = "";
    public Guid? LocationId { get; set; }
    public string? LocalTool { get; set; }
    public string? InstalledVersion { get; set; }
    public string? InstalledOn { get; set; }
    public string? InstalledMethod { get; set; }
    public DateTimeOffset? InstalledCheckedAt { get; set; }
    public string? DetectionError { get; set; }
    public string? LatestVersion { get; set; }
    public string? ReleaseUrl { get; set; }
    public DateTimeOffset? ReleasePublishedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GameWatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OwnerId { get; set; } = DefaultAccount.AdminId;
    public bool IncludeInWidget { get; set; } = true;
    public string? SteamAppId { get; set; }
    public string Name { get; set; } = "";
    public string Provider { get; set; } = "allkeyshop";
    public string SourceUrl { get; set; } = "";
    public string Edition { get; set; } = "Standard";
    public string EditionKey { get; set; } = "STANDARD";
    public string Region { get; set; } = "eu-global";
    public string Payment { get; set; } = "best";
    public bool OfficialOnly { get; set; }
    public int? TargetCents { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public string? Error { get; set; }
    public string CounterpartStatus { get; set; } = "needs_review";
    public string? CounterpartNote { get; set; }
    public string? CounterpartUrl { get; set; }
    public bool CounterpartOptOut { get; set; }
    public DateTimeOffset? CounterpartDiscoveryAttemptAt { get; set; }
    // One-time upgrade discovery, or a retryable transport failure; never a perpetual re-seed rule.
    public bool CounterpartPending { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PriceObservation> Observations { get; set; } = [];
}

public sealed class PriceObservation
{
    public long Id { get; set; }
    public Guid GameWatchId { get; set; }
    public DateTimeOffset ObservedAt { get; set; }
    public int PriceCents { get; set; }
    public string Currency { get; set; } = "EUR";
    public string Merchant { get; set; } = "";
    public string Region { get; set; } = "";
    public string Edition { get; set; } = "";
    public string OfferUrl { get; set; } = "";
    public string? Coupon { get; set; }
    public int MatchingOffers { get; set; }
    public int? OriginalPriceCents { get; set; }
    public int? DiscountPercent { get; set; }
    public DateTimeOffset? SaleEndsAt { get; set; }
    public string? PaymentMethod { get; set; }
    public int? BasePriceCents { get; set; }
    public int? FeeCents { get; set; }
    public string FeeBasis { get; set; } = "unknown";
    public string Provenance { get; set; } = "unknown";
    public string? ActivationPlatform { get; set; }
    public string DeliveryType { get; set; } = "unknown";
    public string? LanguageRestriction { get; set; }
}

public sealed class WatchboardOptions
{
    public string DatabasePath { get; set; } = "storage/watchboard.db";
    public bool AllowRemoteAccess { get; set; }
    public string[] AllowedOrigins { get; set; } = [];
    public bool SecureCookies { get; set; }
    public bool EnablePolling { get; set; } = true;
    public bool SeedDefaults { get; set; } = true;
    public int PriceIntervalMinutes { get; set; } = 1440;
    public int VersionIntervalMinutes { get; set; } = 360;
}
