using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using AngleSharp.Dom;
using Watchboard.Api.Contracts;
using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public static partial class AllKeyShopParser
{
    public static GameResolution Resolve(string html, string sourceUrl, string? requestedEdition = null)
    {
        var document = new HtmlParser().ParseDocument(html);
        var products = Products(document);
        if (products.Count != 1 || !WatchValidation.Text(String(products[0], "name"), 150))
            throw new SourceNotFoundException("No se ha podido resolver un único Product público de AllKeyShop.");
        var scoped = IdentityScope(document, products[0], sourceUrl);
        using var data = OptionalPageData(document);
        var editions = data is not null && data.RootElement.TryGetProperty("editions", out var choices) && choices.ValueKind == JsonValueKind.Object
            ? choices.EnumerateObject().Select(x => String(x.Value, "name")).Where(x => WatchValidation.Text(x, 60)).OfType<string>().Select(NormalizeEdition)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(100).ToList() : [];
        // A scoped base Product can exist before a price table does. Its exact full title must still
        // match a validated Steam game; missing stock/prices are never missing identity or a zero quote.
        if (editions.Count == 0 && (scoped || GameIdentities.Find(sourceUrl) is not null)) editions.Add("Standard");
        if (editions.Count == 0) throw new SourceIdentityException("La página no publica ediciones ni un producto PC base verificable.");
        var selected = requestedEdition is null ? editions.FirstOrDefault(GameIdentities.BaseEdition) ?? editions[0] : NormalizeEdition(requestedEdition);
        if (!editions.Contains(selected, StringComparer.OrdinalIgnoreCase))
            throw new SourceIdentityException("La página no publica la edición seleccionada; no se sustituirá por otra.");
        if (data is not null && data.RootElement.TryGetProperty("isAccPage", out var accountPage) && accountPage.ValueKind == JsonValueKind.True)
            throw new SourceIdentityException("Esta página vende cuentas, no una clave o regalo del juego base.");

        // Only product-level evidence is considered. Even a sole whole-page Steam anchor may be a recommendation.
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in new[] { "steamAppId", "steam_appid", "steamId", "steam_id" })
        {
            if (data is null || !data.RootElement.TryGetProperty(key, out var value)) continue;
            var candidate = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
            if (candidate is not null && ValidAppId(candidate)) ids.Add(candidate);
        }
        var name = String(products[0], "name")!.Trim();
        var known = GameIdentities.Find(sourceUrl);
        if (known is not null)
        {
            var sku = products[0].TryGetProperty("sku", out var skuValue) ? skuValue.ToString() : null;
            if (sku is not null && sku != known.AllKeyShopSku || !GameIdentities.SameTitle(name, known.Name) ||
                ids.Any(id => id != known.SteamAppId))
                throw new SourceIdentityException("La página contradice el SKU/título verificado. Revisa manualmente el enlace; no se asociará otro juego.");
            var result = GameIdentities.KnownResolution(sourceUrl)! with { Name = name, Editions = editions, AllKeyShopEdition = selected };
            return GameIdentities.BaseEdition(selected) ? result : result with { SteamAppId = null, SteamUrl = null,
                MatchStatus = "needs_review", MatchNote = "La edición seleccionada requiere verificar su título completo, no el juego base del catálogo." };
        }
        return GameIdentities.Unverified("allkeyshop", sourceUrl, name, editions, ids.Count == 1 ? ids.Single() : null)
            with { AllKeyShopEdition = selected };
    }

    public static bool HasIdentityScope(string html, string sourceUrl)
    {
        var document = new HtmlParser().ParseDocument(html);
        var products = Products(document);
        return products.Count == 1 && IdentityScope(document, products[0], sourceUrl);
    }

    public static string? ProductSku(string html)
    {
        var products = Products(new HtmlParser().ParseDocument(html));
        return products.Count == 1 && products[0].TryGetProperty("sku", out var sku) ? sku.ToString() : null;
    }

    public static PriceResult Parse(string html, GameWatch watch)
    {
        var document = new HtmlParser().ParseDocument(html);
        // The global JSON-LD minimum mixes editions, DRM and accounts. Never use it as a quote.
        var products = Products(document);
        if (products.Count != 1 || !products[0].TryGetProperty("offers", out var aggregate) ||
            String(aggregate, "priceCurrency") != "EUR")
            throw new SourceException("No se ha podido confirmar la moneda EUR del producto. No se guarda ningún precio.");

        using var data = PageData(document);
        {
            var root = data.RootElement;
            if (root.TryGetProperty("isAccPage", out var accountPage) && accountPage.ValueKind == JsonValueKind.True)
                throw new SourceIdentityException("La página está clasificada como venta de cuentas, no de claves.");
            var prices = root.GetProperty("prices");
            var regions = root.GetProperty("regions");
            var editions = root.GetProperty("editions");
            var results = new List<PriceResult>();
            foreach (var offer in prices.EnumerateArray())
            {
                if (!offer.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.False ||
                    !Number(offer, "dispo", out var stock) || stock != 1)
                    continue;
                var regionId = String(offer, "region");
                var editionId = String(offer, "edition");
                if (regionId is null || editionId is null || !regions.TryGetProperty(regionId, out var region) ||
                    !editions.TryGetProperty(editionId, out var edition)) continue;
                var editionName = String(edition, "name");
                var context = QuoteContext.Classify(offer, region);
                if (context is null || !QuoteContext.CompatibleRegion(watch.Region, context.Region) ||
                    editionName is null || !NormalizeEdition(editionName).Equals(NormalizeEdition(watch.Edition), StringComparison.OrdinalIgnoreCase)) continue;
                if (watch.OfficialOnly && (!offer.TryGetProperty("isOfficial", out var official) || official.ValueKind != JsonValueKind.True)) continue;
                if (!Number(offer, "id", out var id) || id <= 0 || decimal.Truncate(id) != id ||
                    !Number(offer, "merchant", out var merchantId) || merchantId <= 0 || decimal.Truncate(merchantId) != merchantId)
                    continue;
                var merchantName = String(offer, "merchantName");
                if (!WatchValidation.Text(merchantName, 100)) continue;
                // First-party direct licenses are not third-party keys/gifts. In particular the AKS
                // Steam row is not a substitute for the independent, fresh official Steam quote.
                if (merchantName!.Trim().Equals("Steam", StringComparison.OrdinalIgnoreCase) ||
                    offer.TryGetProperty("isFirstParty", out var firstParty) && firstParty.ValueKind == JsonValueKind.True) continue;
                var coupon = String(offer, "voucher_code");
                if (string.IsNullOrWhiteSpace(coupon)) coupon = null;
                if (coupon is not null && !WatchValidation.Text(coupon, 100)) continue;
                foreach (var method in watch.Payment == "best" ? new[] { "card", "paypal" } : new[] { watch.Payment })
                {
                    var suffix = method == "paypal" ? "Paypal" : "Card";
                    if (method is not ("card" or "paypal") || !offer.TryGetProperty("allow" + suffix, out var allowed) ||
                        allowed.ValueKind != JsonValueKind.True || !Money(offer, "price" + suffix, out var total) || total is 0 or 2)
                        continue; // 0.02 is the provider's known unavailable-price sentinel, not a cheap key.
                    int? basis = null;
                    int? fee = null;
                    if (Money(offer, "price", out var publishedBase) && Money(offer, "fees" + suffix, out var publishedFee) &&
                        publishedBase + publishedFee == total)
                    { basis = publishedBase; fee = publishedFee; }
                    results.Add(new PriceResult(total, "EUR", merchantName!, context.Region, watch.Edition.Trim(),
                        $"https://www.allkeyshop.com/redirection/offer/eur/{id:0}?locale=en&merchant={merchantId:0}", coupon, 0,
                        PaymentMethod: method, BasePriceCents: basis, FeeCents: fee, FeeBasis: "included",
                        ActivationPlatform: context.ActivationPlatform, DeliveryType: context.DeliveryType, LanguageRestriction: context.LanguageRestriction));
                }
            }
            if (results.Count == 0) throw new SourceException("No hay claves o regalos PC verificables para esa edición, región y pago. No se guarda un precio cero.");
            var preferred = results.Any(x => x.ActivationPlatform == "steam") ? results.Where(x => x.ActivationPlatform == "steam").ToList() : results;
            return preferred.OrderBy(x => x.PriceCents).ThenBy(x => x.PaymentMethod, StringComparer.Ordinal).First() with
                { MatchingOffers = preferred.Select(x => x.OfferUrl).Distinct(StringComparer.Ordinal).Count() };
        }
    }

    private static bool ValidAppId(string value) => value.Length is >= 1 and <= 10 && value[0] != '0' && value.All(char.IsAsciiDigit);

    // Early Access is a release phase, not a Deluxe/upgrade/product variant. Never strip title words.
    public static string NormalizeEdition(string value) => value.Trim().Equals("Early Access", StringComparison.OrdinalIgnoreCase) ? "Standard" : value.Trim();

    private static bool IdentityScope(IDocument document, JsonElement product, string sourceUrl)
    {
        var expected = WatchValidation.GameUrl(sourceUrl)?.Url ?? throw new SourceIdentityException("El enlace no identifica una comparación PC permitida.");
        var scopes = document.QuerySelectorAll("link[rel='canonical']").Select(x => x.GetAttribute("href"))
            .Concat(new[] { String(product, "@id"), String(product, "url") }).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (scopes.Any(url => WatchValidation.GameUrl(url)?.Url != expected))
            throw new SourceIdentityException("El Product o enlace canónico pertenece a otra página; no se asociará otro SKU.");
        var sku = product.TryGetProperty("sku", out var value) ? value.ToString() : "";
        var validSku = sku.Length is >= 1 and <= 16 && sku.All(char.IsAsciiDigit) && sku.Any(c => c != '0');
        var pc = false;
        if (product.TryGetProperty("category", out var category))
        {
            var name = category.ValueKind == JsonValueKind.String ? category.GetString() : category.ValueKind == JsonValueKind.Object ? String(category, "name") : null;
            pc = name is not null && (name.Trim().Equals("pc", StringComparison.OrdinalIgnoreCase) || name.Trim().StartsWith("pc ", StringComparison.OrdinalIgnoreCase));
            if (!pc && name is not null) throw new SourceIdentityException("La categoría del Product no identifica un juego PC.");
        }
        return scopes.Count > 0 && validSku && pc;
    }

    private static JsonDocument? OptionalPageData(IDocument document)
    {
        try { return PageData(document); }
        catch (Exception exception) when (exception is SourceException or JsonException or InvalidOperationException) { return null; }
    }

    private static List<JsonElement> Products(IDocument document)
    {
        var products = new List<JsonElement>();
        foreach (var script in document.QuerySelectorAll("script[type='application/ld+json']"))
        {
            try
            {
                using var json = JsonDocument.Parse(script.TextContent, new JsonDocumentOptions { MaxDepth = 48 });
                FindProducts(json.RootElement, products);
            }
            catch (JsonException) { /* Unrelated third-party schema must not hide valid Product data. */ }
        }
        return products;
    }

    private static JsonDocument PageData(IDocument document)
    {
        foreach (var script in document.QuerySelectorAll("script"))
        {
            var text = script.TextContent;
            var match = DataAssignment().Match(text);
            if (!match.Success) continue;
            // Parse JSON, never execute page JavaScript. ParseValue stops at the closing object.
            var bytes = Encoding.UTF8.GetBytes(text[(match.Index + match.Length)..]);
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 48 });
            return JsonDocument.ParseValue(ref reader);
        }
        throw new SourceException("AllKeyShop ha cambiado el formato de las ofertas. Se conserva la última lectura.");
    }

    private static void FindProducts(JsonElement element, List<JsonElement> products)
    {
        if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) FindProducts(item, products);
        else if (element.ValueKind == JsonValueKind.Object)
        {
            if (String(element, "@type") == "Product") products.Add(element.Clone());
            if (element.TryGetProperty("@graph", out var graph)) FindProducts(graph, products);
        }
    }

    private static string? String(JsonElement element, string key) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool Number(JsonElement element, string key, out decimal result)
    {
        result = 0;
        return element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out result);
    }

    private static bool Money(JsonElement element, string key, out int cents)
    {
        cents = 0;
        if (!Number(element, key, out var value) || value is < 0 or > 10000 || decimal.Round(value, 2) != value) return false;
        cents = decimal.ToInt32(value * 100);
        return true;
    }

    [GeneratedRegex(@"\b(?:var|const|let)\s+gamePageTrans\s*=\s*", RegexOptions.CultureInvariant)]
    private static partial Regex DataAssignment();
}
