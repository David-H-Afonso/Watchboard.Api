using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchboard.Api.Contracts;
using Watchboard.Api.Data;
using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public sealed class DashboardReader(WatchboardDbContext db, TimeProvider clock, IOptions<WatchboardOptions> options)
{
    public async Task<DashboardDto> ReadAsync(Guid ownerId, CancellationToken token)
    {
        var now = clock.GetUtcNow();
        var config = options.Value;
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == ownerId, token);
        var locations = await db.Locations.AsNoTracking().Where(x => x.OwnerId == ownerId).OrderBy(x => x.Name)
            .Select(x => new LocationDto(x.Id, x.Name)).ToListAsync(token);
        var locationNames = locations.ToDictionary(x => x.Id, x => x.Name);
        var software = await db.Software.AsNoTracking().Where(x => x.OwnerId == ownerId).OrderBy(x => x.Group).ThenBy(x => x.Name).ToListAsync(token);
        var watches = await db.Games.AsNoTracking().Where(x => x.OwnerId == ownerId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Name).ToListAsync(token);
        var games = new List<GameDto>();
        foreach (var watch in watches)
        {
            // Full persisted eligible evidence powers summaries; only the detailed chart is capped.
            var persisted = await db.Prices.AsNoTracking().Where(x => x.GameWatchId == watch.Id && x.ObservedAt <= now)
                .OrderBy(x => x.ObservedAt).ThenBy(x => x.Id)
                .Select(x => new PricePoint(x.Id, x.ObservedAt, x.PriceCents, x.Currency, x.Merchant, x.Region,
                    x.Edition, x.OfferUrl, x.Coupon, x.MatchingOffers, x.OriginalPriceCents, x.DiscountPercent, x.SaleEndsAt,
                    x.PaymentMethod, x.BasePriceCents, x.FeeCents, x.FeeBasis, x.ActivationPlatform, x.DeliveryType, x.LanguageRestriction)).ToListAsync(token);
            var eligible = persisted.Where(x => Eligible(watch, x)).ToList();
            var history = eligible.TakeLast(2160).ToList();
            var current = history.LastOrDefault();
            var summaries = eligible.Select(x => new SaleObservation(x.ObservedAt, x.PriceCents, x.OriginalPriceCents, x.DiscountPercent)).ToList();
            var lowest = summaries.Count == 0 ? (int?)null : summaries.Min(x => x.PriceCents);
            var lowestAt = summaries.Where(x => x.PriceCents == lowest).Select(x => (DateTimeOffset?)x.ObservedAt).FirstOrDefault();
            var lowestPoint = eligible.FirstOrDefault(x => x.PriceCents == lowest);
            var requestedFrom = now.AddDays(-730);
            var twoYear = summaries.Where(x => x.ObservedAt >= requestedFrom).ToList();
            var twoYearLow = twoYear.Count == 0 ? (int?)null : twoYear.Min(x => x.PriceCents);
            var twoYearLowAt = twoYear.Where(x => x.PriceCents == twoYearLow).Select(x => (DateTimeOffset?)x.ObservedAt).FirstOrDefault();
            var twoYearLowPoint = eligible.FirstOrDefault(x => x.ObservedAt >= requestedFrom && x.PriceCents == twoYearLow);
            var reachedAt = watch.TargetCents.HasValue
                ? summaries.Where(x => x.PriceCents <= watch.TargetCents.Value).Select(x => (DateTimeOffset?)x.ObservedAt).LastOrDefault() : null;
            var stale = PriceStale(watch.LastSuccessAt, now, config.PriceIntervalMinutes) || PriceStale(current?.ObservedAt, now, config.PriceIntervalMinutes) ||
                watch.Provider == "allkeyshop" && current is not null && !KnownMarketContext(current) ||
                current?.SaleEndsAt is { } expiry && now >= expiry;
            games.Add(new(watch.Id, watch.Name, watch.Provider, watch.SourceUrl, watch.Edition, watch.Region, watch.Payment,
                watch.OfficialOnly, watch.TargetCents, watch.LastAttemptAt, watch.LastSuccessAt, watch.Error, stale,
                !stale && watch.Error is null && current is not null && watch.TargetCents.HasValue && current.PriceCents <= watch.TargetCents,
                current, lowest, history.Count >= 2 && QuoteContext.Comparable(current!, history[^2]) ? current!.PriceCents - history[^2].PriceCents : null, history,
                watch.IncludeInWidget, GameIdentities.VerifiedSteamAppId(watch), ObservedSales(summaries), reachedAt,
                watch.CounterpartStatus, watch.CounterpartNote, lowestAt, Coverage(eligible, requestedFrom, now), twoYearLow, twoYearLowAt,
                lowestPoint?.ActivationPlatform, lowestPoint?.DeliveryType, twoYearLowPoint?.ActivationPlatform, twoYearLowPoint?.DeliveryType,
                lowestPoint?.LanguageRestriction, twoYearLowPoint?.LanguageRestriction, watch.CounterpartUrl));
        }
        return new(now, Environment.MachineName, OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "macOS",
            config.EnablePolling, config.PriceIntervalMinutes, config.VersionIntervalMinutes,
            software.Select(x => new SoftwareDto(x.Id, x.Name, x.Group, x.Provider, x.Source, ReleaseSources.PublicUrl(x), x.LocalTool,
                x.InstalledVersion, x.InstalledOn, x.InstalledMethod, x.InstalledCheckedAt, x.DetectionError,
                x.LatestVersion, x.ReleaseUrl, VersionComparison.Compare(x.InstalledVersion, x.LatestVersion),
                x.LastAttemptAt, x.LastSuccessAt, x.Error, Stale(x.LastSuccessAt, now, config.VersionIntervalMinutes),
                 x.IncludeInWidget, x.ReleasePublishedAt, x.LocationId, x.LocationId is { } id ? locationNames.GetValueOrDefault(id) : null)).ToList(),
             games, user.ToDto(), user.Preferences(), locations);
    }

    public async Task<WidgetDto> WidgetAsync(Guid ownerId, CancellationToken token)
    {
        var dashboard = await ReadAsync(ownerId, token);
        var rules = dashboard.Preferences;
        var items = new List<WidgetItem>();
        foreach (var watch in dashboard.Software.Where(x => rules.ShowVersions && x.IncludeInWidget))
        {
            if (rules.SoftwareLocationIds is { } ids && (watch.LocationId is not { } locationId || !ids.Contains(locationId))) continue;
            var knownUpdate = !watch.Stale && watch.Error is null && watch.DetectionError is null &&
                !string.IsNullOrWhiteSpace(watch.InstalledVersion) && watch.Comparison == "update";
            if (rules.SoftwareUpdatesOnly && !knownUpdate) continue;
            string? kind = null;
            var detail = "Versión instalada desconocida";
            if (knownUpdate) { kind = "version"; detail = "Actualización disponible"; }
            else if (rules.ShowErrors && (watch.Error is not null || watch.DetectionError is not null || watch.Stale && watch.LatestVersion is not null)) { kind = "error"; detail = watch.Error ?? watch.DetectionError ?? "Lectura de versión desactualizada"; }
            else if (!rules.OnlyActionable) { kind = "software"; detail = watch.InstalledVersion is null ? detail : $"Instalada: {watch.InstalledVersion}"; }
            if (kind is not null) items.Add(new($"{watch.Id}:{kind}", watch.Id, kind, watch.Name,
                $"{watch.InstalledVersion ?? "—"} → {watch.LatestVersion ?? "—"}",
                watch.LocationName is null ? detail : $"{watch.LocationName} · {detail}", watch.ReleaseUrl ?? watch.SourceUrl, watch.LastSuccessAt));
        }
        foreach (var watch in dashboard.Games.Where(x => rules.ShowGames && x.IncludeInWidget))
        {
            string? kind = null;
            var delivery = watch.Current?.DeliveryType switch { "key" => "Clave", "gift" => "Regalo", _ => "Entrega sin confirmar" };
            var detail = watch.Provider == "steam" ? "Steam oficial · España" : watch.Current is null ? $"PC · Precio pendiente · {watch.Edition}" :
                $"{QuoteContext.PlatformName(watch.Current.ActivationPlatform)} · {delivery} · {watch.Edition}{(watch.Current.LanguageRestriction == "en" ? " · Solo inglés" : "")}";
            var usable = !watch.Stale && watch.Error is null && watch.Current is not null;
            if (usable && watch.TargetReached) { kind = "target"; detail += $" · objetivo {Money(watch.TargetCents!.Value)}"; }
            else if (usable && rules.ShowSales && watch.Provider == "steam" && watch.Current is { DiscountPercent: > 0, OriginalPriceCents: not null } price && price.OriginalPriceCents > price.PriceCents)
            { kind = "sale"; detail += $" · descuento {price.DiscountPercent}%"; }
            else if (usable && rules.ShowPriceDrops && watch.ChangeCents < 0) { kind = "drop"; detail += $" · baja {Money(-watch.ChangeCents.Value)}"; }
            else if (rules.ShowErrors && (watch.Error is not null || watch.Stale && watch.Current is not null)) { kind = "error"; detail += " · lectura no disponible"; }
            else if (!rules.OnlyActionable) kind = "game";
            if (kind is not null) items.Add(new($"{watch.Id}:{kind}", watch.Id, kind, watch.Name,
                watch.Current is null ? "Sin precio verificado" : Money(watch.Current.PriceCents), detail, watch.Current?.OfferUrl ?? watch.SourceUrl, watch.Current?.ObservedAt));
        }
        return new(dashboard.GeneratedAt, dashboard.User, items.OrderBy(x => x.Kind switch
        { "target" => 0, "version" => 1, "sale" => 2, "drop" => 3, "error" => 4, "software" => 5, _ => 6 }).Take(rules.MaxItems).ToList());
    }

    private sealed record SaleObservation(DateTimeOffset ObservedAt, int PriceCents, int? OriginalPriceCents, int? DiscountPercent);

    private static bool Eligible(GameWatch watch, PricePoint point)
    {
        if (point.Currency != "EUR" || point.PriceCents is < 0 or > 1_000_000 || point.MatchingOffers < 1 ||
            !point.Edition.Trim().Equals(watch.Edition.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (watch.Provider == "steam")
            return (point.ActivationPlatform is null && point.DeliveryType == "unknown" || point.ActivationPlatform == "steam" && point.DeliveryType == "direct") &&
                point.LanguageRestriction is null && point.Merchant == "Steam" && point.Region == "España" && WatchValidation.GameUrl(point.OfferUrl)?.AppId ==
                WatchValidation.GameUrl(watch.SourceUrl)?.AppId;
        var unknownLegacy = point.ActivationPlatform is null && point.DeliveryType == "unknown" && point.LanguageRestriction is null;
        return (KnownMarketContext(point) || unknownLegacy) && point.PriceCents > 0 && point.PriceCents != 2 && !point.Merchant.Trim().Equals("Steam", StringComparison.OrdinalIgnoreCase) &&
            QuoteContext.CompatibleRegion(watch.Region, point.Region) &&
            Uri.TryCreate(point.OfferUrl, UriKind.Absolute, out var url) && url.Scheme == "https" && url.IsDefaultPort &&
            url.UserInfo.Length == 0 && url.Host == "www.allkeyshop.com" && url.AbsolutePath.StartsWith("/redirection/offer/eur/", StringComparison.Ordinal);
    }

    private static bool KnownMarketContext(PricePoint point) => point.ActivationPlatform is not null &&
        QuoteContext.Platform(point.ActivationPlatform) == point.ActivationPlatform && point.DeliveryType is "key" or "gift" &&
        point.LanguageRestriction is null or "en";

    private static HistoryCoverage Coverage(IReadOnlyList<PricePoint> readings, DateTimeOffset from, DateTimeOffset to)
    {
        const string limitation = "Solo observaciones reales guardadas en SQLite. No hay un histórico público de dos años verificado para Steam ni de totales AKS con comisiones; la recuperación histórica no está disponible.";
        if (readings.Count == 0) return new(from, to, null, null, "collecting", false, "Aún no hay lecturas elegibles. " + limitation);
        var oldest = readings[0].ObservedAt;
        var newest = readings[^1].ObservedAt;
        var tolerance = TimeSpan.FromHours(26); // Daily observation plus two-hour grace, not accidental 48-hour freshness.
        var window = readings.Where(x => x.ObservedAt >= from - tolerance).ToList();
        var contiguous = window.Count >= 2 && window[0].ObservedAt <= from && to - newest <= tolerance &&
            window.Zip(window.Skip(1), (left, right) => right.ObservedAt - left.ObservedAt).All(gap => gap <= tolerance);
        var status = contiguous ? "complete" : oldest > from ? "collecting" : "partial";
        var detail = status switch
        {
            "complete" => "Cobertura local diaria comprobada en todo el intervalo solicitado, sin huecos mayores de 26 horas. ",
            "partial" => "Hay observaciones antiguas, pero faltan lecturas o existen huecos en el intervalo solicitado. ",
            _ => "La recopilación local todavía no cubre todo el intervalo solicitado. "
        };
        return new(from, to, oldest, newest, status, false, detail + limitation);
    }

    private static IReadOnlyList<SalePeriod> ObservedSales(IEnumerable<SaleObservation> history)
    {
        var periods = new List<SalePeriod>();
        SalePeriod? open = null;
        foreach (var point in history)
        {
            var onSale = point.DiscountPercent > 0 && point.OriginalPriceCents > point.PriceCents;
            if (onSale)
            {
                open = open is null ? new(point.ObservedAt, point.ObservedAt, null, point.PriceCents, point.DiscountPercent) :
                    open with { LastObservedAt = point.ObservedAt, LowestPriceCents = Math.Min(open.LowestPriceCents, point.PriceCents), DiscountPercent = point.DiscountPercent };
            }
            else if (open is not null && (point.DiscountPercent == 0 || point.OriginalPriceCents == point.PriceCents))
            {
                periods.Add(open with { EndedObservedAt = point.ObservedAt });
                open = null;
            }
        }
        if (open is not null) periods.Add(open);
        return periods;
    }

    private static string Money(int cents) => (cents / 100m).ToString("C", CultureInfo.GetCultureInfo("es-ES"));
    public static bool Stale(DateTimeOffset? success, DateTimeOffset now, int intervalMinutes) =>
        success is null || now - success.Value > TimeSpan.FromMinutes(Math.Max(intervalMinutes * 2, 30));
    public static bool PriceStale(DateTimeOffset? success, DateTimeOffset now, int intervalMinutes) =>
        success is null || now - success.Value > TimeSpan.FromMinutes(Math.Max(intervalMinutes, 15) + 120);
}
