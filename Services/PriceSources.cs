using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Watchboard.Api.Contracts;
using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public sealed record GamePreparation(GameResolution? Resolution, PriceResult? Price, string? Error,
    bool IdentityValidated = false, bool DiscoveryRetryable = false, DateTimeOffset? ObservedAt = null);

public sealed class PriceSources(SourceHttp http, SteamSaleEnrichment sales, TimeProvider clock)
{
    public const int MaximumSteamCandidates = 3;
    public const int MaximumAllKeyShopCandidates = 3;
    // Request/operation-scoped public snapshots only, never private user state or a cross-request price cache.
    // Discovery and first quote reuse the very same fetched metadata instead of crawling a page twice.
    private readonly Dictionary<string, (string Html, DateTimeOffset At)> pages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (JsonElement Data, DateTimeOffset At)> steam = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExceptionDispatchInfo> pageFailures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExceptionDispatchInfo> steamFailures = new(StringComparer.Ordinal);

    public async Task<GameResolution> ResolveAsync(string sourceUrl, CancellationToken token)
    {
        var source = WatchValidation.GameUrl(sourceUrl) ?? throw new SourceException("URL de precios no permitida.");
        try
        {
            if (source.Provider == "allkeyshop")
            {
                var page = await PageAsync(source.Url, token);
                var metadata = AllKeyShopParser.Resolve(page.Html, source.Url);
                return (await ResolveKeysAsync(page.Html, metadata, token)).Resolution;
            }
            return (await ResolveSteamAsync(SteamResolution((await SteamDataAsync(source.AppId!, token)).Data, source.Url, source.AppId!), token)).Resolution;
        }
        catch (Exception exception) when (!token.IsCancellationRequested && exception is HttpRequestException or OperationCanceledException)
        {
            if (GameIdentities.TransportFallback(source.Url) is { } cached) return cached;
            throw;
        }
    }

    public async Task<PriceResult> ReadAsync(GameWatch watch, CancellationToken token)
    {
        var source = WatchValidation.GameUrl(watch.SourceUrl) ?? throw new SourceException("URL de precios no permitida.");
        if (source.Provider == "allkeyshop")
        {
            var html = (await PageAsync(source.Url, token)).Html;
            var resolution = AllKeyShopParser.Resolve(html, source.Url, watch.Edition);
            return AllKeyShopParser.Parse(html, watch) with
                { SteamAppId = GameIdentities.BaseEdition(watch.Edition) ? resolution.SteamAppId : null };
        }
        var data = (await SteamDataAsync(source.AppId!, token)).Data;
        _ = SteamResolution(data, source.Url, source.AppId!);
        return await SteamPriceAsync(data, source.Url, source.AppId!, token);
    }

    public DateTimeOffset? SnapshotTime(string sourceUrl)
    {
        var source = WatchValidation.GameUrl(sourceUrl);
        if (source is null) return null;
        if (source.Value.Provider == "allkeyshop") return pages.TryGetValue(source.Value.Url, out var page) ? page.At : null;
        return source.Value.AppId is { } id && steam.TryGetValue(id, out var snapshot) ? snapshot.At : null;
    }

    public async Task<GamePreparation> PrepareAsync(GameWatch watch, CancellationToken token, bool discoverCounterpart = true)
    {
        GameResolution? resolution = GameIdentities.KnownResolution(watch.SourceUrl);
        var identityValidated = false;
        var retryable = false;
        DateTimeOffset? observedAt = null;
        try
        {
            var source = WatchValidation.GameUrl(watch.SourceUrl) ?? throw new SourceException("URL de precios no permitida.");
            if (source.Provider == "allkeyshop")
            {
                var page = await PageAsync(source.Url, token);
                observedAt = page.At;
                resolution = AllKeyShopParser.Resolve(page.Html, source.Url, watch.Edition);
                identityValidated = resolution.MatchStatus == "verified" || AllKeyShopParser.HasIdentityScope(page.Html, source.Url);
                if (discoverCounterpart)
                {
                    var discovered = await ResolveKeysAsync(page.Html, resolution, token);
                    resolution = discovered.Resolution;
                    retryable = discovered.Retryable;
                }
                return new(resolution, AllKeyShopParser.Parse(page.Html, watch) with
                    { SteamAppId = resolution.MatchStatus == "verified" ? resolution.SteamAppId : null }, null,
                    identityValidated, retryable, observedAt);
            }
            var snapshot = await SteamDataAsync(source.AppId!, token);
            observedAt = snapshot.At;
            resolution = SteamResolution(snapshot.Data, source.Url, source.AppId!);
            identityValidated = true;
            if (discoverCounterpart)
            {
                var discovered = await ResolveSteamAsync(resolution, token);
                resolution = discovered.Resolution;
                retryable = discovered.Retryable;
            }
            return new(resolution, await SteamPriceAsync(snapshot.Data, source.Url, source.AppId!, token), null,
                identityValidated, retryable, observedAt);
        }
        catch (Exception ex) when (SourceFailures.Is(ex, token))
        {
            if (ex is HttpRequestException or OperationCanceledException && GameIdentities.TransportFallback(watch.SourceUrl) is { } cached)
                return new(cached, null, SourceFailures.Message(ex), true, false, observedAt);
            if (ex is SourceIdentityException)
            {
                resolution = GameIdentities.Unverified(watch.Provider, watch.SourceUrl, resolution?.Name ?? watch.Name, [watch.Edition])
                    with { MatchNote = ex.Message };
                identityValidated = false;
            }
            // A missing quote after successful metadata is not a discovery failure. A transport/format
            // failure before identity was established remains eligible for a later bounded retry.
            retryable |= !identityValidated && ex is not (SourceIdentityException or SourceNotFoundException or SourceRedirectException);
            return new(resolution, null, SourceFailures.Message(ex), identityValidated, retryable, observedAt);
        }
    }

    private async Task<Discovery> ResolveKeysAsync(string html, GameResolution resolution, CancellationToken token)
    {
        if (resolution.MatchStatus == "verified") return new(resolution, false);
        if (resolution.AllKeyShopEdition is null || !AllKeyShopParser.HasIdentityScope(html, resolution.SourceUrl))
            return new(resolution with { SteamUrl = null, MatchNote = "Falta una edición publicada o un Product PC con SKU y alcance canónico verificables." }, false);
        var expectedTitle = GameIdentities.IdentityTitle(resolution);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (WatchValidation.GameUrl(resolution.SteamUrl)?.AppId is { } publishedId) ids.Add(publishedId);
        var retryable = false;
        try
        {
            using var results = await http.JsonAsync($"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(expectedTitle)}&cc=es&l=english", token);
            if (results.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                if (items.GetArrayLength() > 100) return new(resolution with { SteamUrl = null, MatchNote = "Demasiados resultados públicos para comprobar una coincidencia única de forma acotada." }, false);
                foreach (var item in items.EnumerateArray())
                    if (item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
                        GameIdentities.SameTitle(name.GetString()!, expectedTitle) && item.TryGetProperty("id", out var id) &&
                        id.TryGetInt64(out var number) && number is > 0 and <= 9999999999)
                        ids.Add(number.ToString(CultureInfo.InvariantCulture));
            }
            else retryable = true; // An unrecognized provider response is not proof of an empty catalogue.
        }
        catch (Exception ex) when (SourceFailures.Is(ex, token)) { retryable = true; }
        if (ids.Count > MaximumSteamCandidates)
            return new(resolution with { SteamUrl = null, MatchNote = "Hay varios candidatos con el mismo título; la comprobación automática acotada no puede elegir una aplicación." }, false);
        var matches = new List<GameResolution>();
        foreach (var id in ids)
        {
            try
            {
                var candidate = SteamResolution((await SteamDataAsync(id, token)).Data, $"https://store.steampowered.com/app/{id}/", id);
                if (candidate.ProductType == resolution.ProductType && GameIdentities.SameTitle(candidate.Name, expectedTitle)) matches.Add(candidate);
            }
            catch (Exception ex) when (SourceFailures.Is(ex, token))
            {
                if (ex is not (SourceIdentityException or SourceNotFoundException or SourceRedirectException)) retryable = true;
            }
        }
        if (matches.Count == 1 && !retryable)
        {
            var match = matches[0];
            return new(resolution with { SteamAppId = match.SteamAppId, SteamUrl = match.SourceUrl, MatchStatus = "verified",
                MatchNote = $"Coincidencia pública única: título completo exacto, tipo {match.ProductType}, ID y edición {resolution.AllKeyShopEdition} validados; precios independientes." }, false);
        }
        if (retryable && matches.Count == 0 && GameIdentities.BaseEdition(resolution.AllKeyShopEdition) && GameIdentities.TransportFallback(resolution.SourceUrl, resolution.Name, AllKeyShopParser.ProductSku(html)) is { } cached &&
            ids.All(id => id == cached.SteamAppId))
            return new(cached with { Name = resolution.Name, Editions = resolution.Editions, AllKeyShopEdition = resolution.AllKeyShopEdition }, false);
        return new(resolution with { SteamAppId = null, SteamUrl = matches.Count == 1 ? matches[0].SourceUrl : null,
            MatchStatus = matches.Count > 1 || retryable ? "needs_review" : "not_found",
            MatchNote = retryable ? "La búsqueda pública no pudo completarse. Se puede reintentar sin inventar precios ni asociar una coincidencia incompleta." :
                matches.Count > 1 ? "Varias aplicaciones publican ese título completo; elige el enlace correcto manualmente." :
                "No se encontró un juego Steam con ese título completo y edición seleccionada. Puedes indicar su enlace manualmente." }, retryable);
    }

    private async Task<Discovery> ResolveSteamAsync(GameResolution resolution, CancellationToken token)
    {
        if (resolution.MatchStatus == "verified") return new(resolution, false);
        var matches = new List<(GameResolution Resolution, string Sku, string Edition)>();
        var retryable = false;
        var candidates = GameIdentities.PublicComparisonCandidates(resolution.Name);
        foreach (var url in candidates.Take(2).Concat(GameIdentities.VariantComparisonCandidates(resolution.Name)).Concat(candidates.Skip(2))
                     .Distinct(StringComparer.Ordinal).Take(MaximumAllKeyShopCandidates))
        {
            try
            {
                var page = await PageAsync(url, token);
                var candidate = AllKeyShopParser.Resolve(page.Html, url);
                var edition = GameIdentities.MatchingEdition(candidate, resolution.Name, resolution.ProductType);
                if (!AllKeyShopParser.HasIdentityScope(page.Html, url) || edition is null) continue;
                if (candidate.SteamAppId is not null && candidate.SteamAppId != resolution.SteamAppId) continue;
                if (WatchValidation.GameUrl(candidate.SteamUrl)?.AppId is { } advertised && advertised != resolution.SteamAppId) continue;
                matches.Add((candidate, AllKeyShopParser.ProductSku(page.Html)!, edition));
            }
            catch (Exception ex) when (SourceFailures.Is(ex, token))
            {
                if (ex is not (SourceIdentityException or SourceNotFoundException or SourceRedirectException)) retryable = true;
            }
        }
        var products = matches.GroupBy(x => x.Sku, StringComparer.Ordinal).ToList();
        if (products.Count == 1 && !retryable)
            return new(resolution with { AllKeyShopUrl = products[0].First().Resolution.SourceUrl,
                AllKeyShopEdition = products[0].First().Edition, MatchStatus = "verified",
                MatchNote = $"Coincidencia pública única: Product PC, SKU, enlace canónico y título completo de la edición {products[0].First().Edition} validados; el precio puede no estar disponible." }, false);
        if (retryable && products.Count == 0 && GameIdentities.TransportFallback(resolution.SourceUrl, resolution.Name) is { } cached)
            return new(cached with { Name = resolution.Name }, false);
        return new(resolution with { AllKeyShopUrl = null, MatchStatus = products.Count > 1 || retryable ? "needs_review" : "not_found",
            MatchNote = retryable ? "No se completó la búsqueda acotada de comparaciones públicas. La identidad Steam se conserva y se puede reintentar." :
                products.Count > 1 ? "Varias comparaciones PC del mismo título y distinta identidad; revisa el enlace de la edición base." :
                "No hay una comparación pública PC exacta verificada entre las candidatas. Puedes indicar el enlace manualmente." }, retryable);
    }

    private async Task<(string Html, DateTimeOffset At)> PageAsync(string url, CancellationToken token)
    {
        if (pages.TryGetValue(url, out var saved)) return saved;
        if (pageFailures.TryGetValue(url, out var failed)) failed.Throw();
        try
        {
            var result = (await http.GetAsync(url, token), clock.GetUtcNow());
            pages.Add(url, result);
            return result;
        }
        catch (Exception exception) when (SourceFailures.Is(exception, token))
        {
            pageFailures[url] = ExceptionDispatchInfo.Capture(exception);
            throw;
        }
    }

    private async Task<(JsonElement Data, DateTimeOffset At)> SteamDataAsync(string appId, CancellationToken token)
    {
        if (steam.TryGetValue(appId, out var saved)) return saved;
        if (steamFailures.TryGetValue(appId, out var failed)) failed.Throw();
        try
        {
            var result = (await FetchSteamDataAsync(appId, token), clock.GetUtcNow());
            steam.Add(appId, result);
            return result;
        }
        catch (Exception exception) when (SourceFailures.Is(exception, token))
        {
            steamFailures[appId] = ExceptionDispatchInfo.Capture(exception);
            throw;
        }
    }

    private async Task<JsonElement> FetchSteamDataAsync(string appId, CancellationToken token)
    {
        using var json = await http.JsonAsync($"https://store.steampowered.com/api/appdetails?appids={appId}&cc=es&l=english&filters=basic,price_overview", token);
        var app = json.RootElement.GetProperty(appId);
        if (!app.GetProperty("success").GetBoolean() || !app.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            throw new SourceIdentityException("Steam no publica información verificable para esta aplicación.");
        if (!data.TryGetProperty("steam_appid", out var id) || !id.TryGetInt64(out var number) || number.ToString(CultureInfo.InvariantCulture) != appId)
            throw new SourceIdentityException("Steam ha devuelto otra aplicación; no se ha asociado al seguimiento.");
        if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() is not ("game" or "dlc"))
            throw new SourceIdentityException("Steam no identifica esta aplicación como juego o DLC comprable; no se asociarán demos, herramientas, vídeos ni upgrades.");
        return data.Clone();
    }

    private static GameResolution SteamResolution(JsonElement data, string url, string appId)
    {
        var name = data.TryGetProperty("name", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        if (!WatchValidation.Text(name, 150)) throw new SourceIdentityException("Steam no publica un nombre verificable para esta aplicación.");
        var productType = data.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;
        var known = GameIdentities.KnownResolution(url);
        if (known is not null && productType != "game")
            throw new SourceIdentityException("El catálogo verificado identifica esta URL como juego completo, pero Steam la devuelve con otro tipo de aplicación.");
        if (known is not null && !GameIdentities.SameTitle(known.Name, name!))
            throw new SourceIdentityException("El título Steam contradice la identidad pública verificada. Revisa el enlace antes de asociarlo.");
        return known is not null ? known with { Name = name!.Trim(), ProductType = productType } :
            GameIdentities.Unverified("steam", url, name!.Trim(), ["Standard"]) with { ProductType = productType };
    }

    private async Task<PriceResult> SteamPriceAsync(JsonElement data, string url, string appId, CancellationToken token)
    {
        if (!data.TryGetProperty("price_overview", out var price))
            throw new SourceException("Steam no publica un precio de compra para España. No se guarda un precio cero.");
        if (price.GetProperty("currency").GetString() != "EUR" || !price.GetProperty("final").TryGetInt32(out var cents) || cents < 0 || cents > 1_000_000)
            throw new SourceException("Steam no ha devuelto un precio válido en EUR.");
        int? original = price.TryGetProperty("initial", out var initial) && initial.TryGetInt32(out var basePrice) &&
            basePrice >= cents && basePrice <= 1_000_000 ? basePrice : null;
        int? discount = price.TryGetProperty("discount_percent", out var percent) && percent.TryGetInt32(out var amount) && amount is >= 0 and <= 100 ? amount : null;
        var endsAt = discount > 0 ? await sales.EndsAtAsync(appId, token) : null;
        return new(cents, "EUR", "Steam", "España", "Standard", url, null, 1, original, discount, endsAt, appId,
            FeeBasis: "not_applicable", ActivationPlatform: "steam", DeliveryType: "direct");
    }

    private sealed record Discovery(GameResolution Resolution, bool Retryable);
}

public sealed class SteamSaleEnrichment(SourceHttp http, TimeProvider clock)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private Dictionary<string, DateTimeOffset> expirations = [];
    private DateTimeOffset nextRead;

    public async Task<DateTimeOffset?> EndsAtAsync(string appId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var now = clock.GetUtcNow();
            if (now >= nextRead)
            {
                nextRead = now.AddMinutes(5);
                expirations = [];
                try
                {
                    using var json = await http.JsonAsync("https://store.steampowered.com/api/featuredcategories?cc=es&l=spanish", token);
                    foreach (var category in json.RootElement.EnumerateObject())
                    {
                        if (category.Value.ValueKind != JsonValueKind.Object || !category.Value.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) continue;
                        foreach (var item in items.EnumerateArray())
                        {
                            if (!item.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) || number <= 0 ||
                                !item.TryGetProperty("discount_expiration", out var expiry) || !expiry.TryGetInt64(out var seconds) || seconds is < 0 or > 253402300799) continue;
                            var end = DateTimeOffset.FromUnixTimeSeconds(seconds);
                            if (end > now) expirations[number.ToString(CultureInfo.InvariantCulture)] = end;
                        }
                    }
                }
                catch (Exception ex) when (SourceFailures.Is(ex, token)) { /* Best-effort enrichment only. */ }
            }
            return expirations.TryGetValue(appId, out var result) && result > now ? result : null;
        }
        finally { gate.Release(); }
    }
}
