using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Watchboard.Api.Contracts;
using Watchboard.Api.Data;
using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public sealed class GameOperationException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

public sealed class GameWatchService(WatchboardDbContext db, PriceSources prices, OperationGate mutations, TimeProvider clock)
{
    public async Task<GameCreationDto> CreateAsync(Guid ownerId, CreateGame value, CancellationToken token)
    {
        var error = WatchValidation.Game(value);
        if (error is not null) throw Invalid(error);
        var source = WatchValidation.GameUrl(value.SourceUrl)!.Value;
        var edition = source.Provider == "steam" ? "Standard" : value.Edition.Trim();
        var include = value.IncludeCounterpart ?? value.IncludeSteam ?? true;
        if (value.IncludeSteam == true && source.Provider != "steam" && !GameIdentities.BaseEdition(edition))
            throw Invalid("Solo se puede asociar Steam a la edición Standard/base, no a otra edición.");
        var watch = new GameWatch
        {
            OwnerId = ownerId, Name = Clean(value.Name) ?? "", Provider = source.Provider, SourceUrl = source.Url,
            Edition = edition, Region = value.Region, Payment = source.Provider == "steam" ? "card" : value.Payment,
            OfficialOnly = source.Provider == "steam" || value.OfficialOnly, TargetCents = value.TargetCents,
            SteamAppId = source.AppId, CreatedAt = clock.GetUtcNow(), CounterpartOptOut = !include
        };
        var ownedSource = include && GameIdentities.BaseEdition(edition) ? await ExistingSourceAsync(ownerId, source.Url, token) : null;
        if (ownedSource is not null)
        {
            var reused = await CounterpartAsync(ownerId, ownedSource.Id, null, token);
            return reused with { MatchNote = "Se reutiliza la fuente propia existente y se conservan sus filtros, objetivo e historial. " + reused.MatchNote };
        }
        if (await DuplicateAsync(watch, token) is not null) throw Invalid("Ya sigues esta fuente con esos filtros. Cambia su objetivo o usa otros filtros.", 409);
        if (await db.Games.CountAsync(x => x.OwnerId == ownerId, token) >= 100) throw Invalid("Se admiten hasta 100 precios por cuenta.");

        var attempted = clock.GetUtcNow();
        var prepared = await prices.PrepareAsync(watch, token, discoverCounterpart: include && GameIdentities.BaseEdition(edition));
        if (watch.Name.Length == 0) watch.Name = prepared.Resolution?.Name ?? "";
        if (watch.Name.Length == 0) throw Invalid(prepared.Error ?? "Indica un nombre para guardar el enlace con su error de fuente.");
        SetIdentity(watch, prepared.Resolution);
        RefreshCoordinator.ApplyPrice(watch, prepared.Price, prepared.Error, attempted, prepared.ObservedAt ?? clock.GetUtcNow());
        var pair = include ? await PreparePairAsync(watch, prepared, null, token) : NoPair(watch.CounterpartStatus, watch.CounterpartNote);

        return await WriteAsync(token, async () =>
        {
            await AssertOwnerAsync(ownerId, token);
            if (await DuplicateAsync(watch, token) is not null) throw Invalid("Ya sigues esta fuente con esos filtros.", 409);
            var related = await MergePairAsync(watch, pair, 1, token);
            db.Games.Add(watch);
            await db.SaveChangesAsync(token);
            return new(watch.Id, related, watch.CounterpartStatus, watch.CounterpartNote);
        });
    }

    public async Task<GameCreationDto> CounterpartAsync(Guid ownerId, Guid id, string? sourceUrl, CancellationToken token, bool pendingOnly = false)
    {
        var original = await db.Games.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, token)
            ?? throw Invalid("No existe ese seguimiento.", 404);
        if (pendingOnly && (!original.CounterpartPending || original.CounterpartOptOut))
            return new(original.Id, [], original.CounterpartStatus, original.CounterpartNote);
        var link = sourceUrl ?? (original.CounterpartStatus == "verified" ? original.CounterpartUrl : null);
        if (link is not null) _ = ValidateManualLink(original, link);
        var attemptedAt = clock.GetUtcNow();
        var prepared = await prices.PrepareAsync(original, token, discoverCounterpart: link is null && GameIdentities.BaseEdition(original.Edition));
        var observedAt = prepared.ObservedAt ?? clock.GetUtcNow();
        var pair = await PreparePairAsync(original, prepared, link, token, manualSelection: sourceUrl is not null);
        return await WriteAsync(token, async () =>
        {
            await AssertOwnerAsync(ownerId, token);
            // Fresh entity: a deleted/replaced ID, another owner, or a concurrent goal edit never gets overwritten.
            db.ChangeTracker.Clear();
            var watch = await db.Games.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, token)
                ?? throw Invalid("El seguimiento se eliminó durante la consulta; no se ha vuelto a crear.", 404);
            if (pendingOnly && (!watch.CounterpartPending || watch.CounterpartOptOut) || watch.CounterpartOptOut && !original.CounterpartOptOut)
                return new(watch.Id, [], watch.CounterpartStatus, watch.CounterpartNote); // A later explicit deletion/opt-out wins.
            if (!pendingOnly) watch.CounterpartOptOut = false;
            if ((watch.LastSuccessAt is null || watch.Error is not null) && watch.LastAttemptAt == original.LastAttemptAt)
                RefreshCoordinator.ApplyPrice(watch, prepared.Price, prepared.Error, attemptedAt, observedAt);
            var related = await MergePairAsync(watch, pair, 0, token);
            await db.SaveChangesAsync(token);
            return new(watch.Id, related, watch.CounterpartStatus, watch.CounterpartNote);
        });
    }

    private async Task<PairPlan> PreparePairAsync(GameWatch original, GamePreparation prepared, string? manualUrl, CancellationToken token, bool manualSelection = false)
    {
        var resolution = prepared.Resolution;
        if (!GameIdentities.BaseEdition(original.Edition))
        {
            if (manualUrl is not null) throw Invalid("La asociación exige la misma edición base; no se asocian upgrades, DLC ni ediciones distintas.", 409);
            return NoPair("needs_review", "Esta edición no es Standard/base. Usa fuentes independientes; no se asociará automáticamente a otra edición.");
        }
        string? counterpartUrl;
        if (manualUrl is not null)
        {
            counterpartUrl = ValidateManualLink(original, manualUrl);
        }
        else
        {
            if (resolution?.MatchStatus != "verified")
                return NoPair(resolution?.MatchStatus ?? "needs_review", resolution?.MatchNote ?? prepared.Error ?? "No hay coincidencia segura. Indica el enlace de la otra tienda para validarlo.", prepared.DiscoveryRetryable);
            counterpartUrl = original.Provider == "steam" ? resolution.AllKeyShopUrl : resolution.SteamUrl;
            if (counterpartUrl is null) return NoPair("not_found", "No se encontró una fuente recíproca verificada. Puedes indicar su enlace manualmente.");
        }
        var source = WatchValidation.GameUrl(counterpartUrl)!.Value;
        var existing = await ExistingSourceAsync(original.OwnerId, counterpartUrl, token);
        var candidate = new GameWatch
        {
            OwnerId = original.OwnerId, Provider = source.Provider, SourceUrl = source.Url, SteamAppId = source.AppId,
            Name = resolution?.Name ?? original.Name, Payment = source.Provider == "steam" ? "card" : "best",
            OfficialOnly = source.Provider == "steam", CreatedAt = clock.GetUtcNow()
        };
        var attemptedAt = clock.GetUtcNow();
        // A reused source may have deliberate PayPal/global/official-only filters. Its first/retried
        // quote must be prepared for THOSE filters, not the defaults of a newly proposed counterpart.
        var quotedWatch = existing ?? candidate;
        var expectedAttempt = existing?.LastAttemptAt;
        var counterpart = await prices.PrepareAsync(quotedWatch, token, discoverCounterpart: false);
        if (counterpart.Resolution is { } metadata) candidate.Name = metadata.Name;
        var knownPair = GameIdentities.Find(original.SourceUrl);
        var verified = knownPair is not null && resolution?.MatchStatus == "verified" &&
            // A transport/quote error retains catalog evidence; contradictory identity evidence never does.
            (counterpart.Resolution?.MatchStatus == "verified" || counterpart.Resolution is null);
        string? appId;
        string note;
        if (verified)
        {
            appId = knownPair!.SteamAppId;
            note = resolution!.MatchNote ?? "Identidad recíproca verificada.";
        }
        else if (prepared.IdentityValidated && counterpart.IdentityValidated && resolution is not null && counterpart.Resolution is not null)
        {
            if (knownPair is not null || !GameIdentities.SameTitle(resolution.Name, counterpart.Resolution.Name) ||
                !resolution.Editions.Any(GameIdentities.BaseEdition) || !counterpart.Resolution.Editions.Any(GameIdentities.BaseEdition) ||
                original.SteamAppId is not null && original.Provider == "steam" && original.SteamAppId != WatchValidation.GameUrl(original.SourceUrl)!.Value.AppId)
            {
                if (manualSelection) throw Invalid("No se pudo validar el mismo título completo, SKU PC y edición base. No se creó ninguna asociación.", 409);
                return NoPair("needs_review", "La otra página no coincide con el título completo, SKU PC o edición base; no se mezclarán las fuentes.");
            }
            appId = original.Provider == "steam" ? WatchValidation.GameUrl(original.SourceUrl)!.Value.AppId : source.AppId;
            var advertisedSteam = original.Provider == "allkeyshop" ? resolution.SteamUrl : counterpart.Resolution.SteamUrl;
            if (advertisedSteam is not null && WatchValidation.GameUrl(advertisedSteam)?.AppId != appId)
                throw Invalid("La identidad Steam publicada/candidata contradice el enlace manual. No se asociará otro juego.", 409);
            if (original.SteamAppId is not null && original.SteamAppId != appId)
                throw Invalid("El seguimiento ya identifica otra aplicación Steam; no se mezclará su historial.", 409);
            note = manualSelection ? "Enlace elegido manualmente y validado con título público completo, tipo de juego, SKU PC, enlace canónico y edición base; precios independientes." :
                "Coincidencia pública exacta validada entre el juego Steam y el Product PC/SKU canónico de la edición base; precios independientes.";
        }
        else
        {
            if (manualSelection) throw Invalid("No se pudo validar la identidad pública completa. Revisa el enlace o reintenta cuando la fuente esté disponible; no se creó ninguna asociación.", 409);
            return NoPair("needs_review", counterpart.Error ?? counterpart.Resolution?.MatchNote ?? "La otra fuente contradice la identidad verificada. Revisa el enlace manualmente.",
                prepared.DiscoveryRetryable || counterpart.DiscoveryRetryable);
        }
        candidate.SteamAppId = appId;
        candidate.CounterpartStatus = "verified";
        candidate.CounterpartNote = note;
        candidate.CounterpartUrl = original.SourceUrl;
        candidate.CounterpartDiscoveryAttemptAt = clock.GetUtcNow();
        RefreshCoordinator.ApplyPrice(candidate, counterpart.Price, counterpart.Error, attemptedAt, counterpart.ObservedAt ?? clock.GetUtcNow());
        if (counterpart.Error is not null) note += " La otra fuente se ha guardado con un error explícito: " + counterpart.Error;
        return new(candidate, existing?.Id, counterpart, attemptedAt, appId, "verified", note, quotedWatch, expectedAttempt, false);
    }

    private async Task<List<Guid>> MergePairAsync(GameWatch watch, PairPlan plan, int newOriginals, CancellationToken token)
    {
        watch.CounterpartStatus = plan.Status;
        watch.CounterpartNote = plan.Note;
        watch.CounterpartPending = plan.Retryable && !watch.CounterpartOptOut;
        watch.CounterpartDiscoveryAttemptAt = clock.GetUtcNow();
        if (plan.Candidate is null)
        {
            if (await db.Games.CountAsync(x => x.OwnerId == watch.OwnerId, token) + newOriginals > 100)
                throw Invalid("Se admiten hasta 100 precios por cuenta.");
            return [];
        }
        var existing = plan.ExistingId is { } id
            ? await db.Games.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == watch.OwnerId, token)
            : await ExistingSourceAsync(watch.OwnerId, plan.Candidate.SourceUrl, token, tracked: true);
        if (plan.ExistingId.HasValue && existing is null)
        {
            if (await db.Games.CountAsync(x => x.OwnerId == watch.OwnerId, token) + newOriginals > 100)
                throw Invalid("Se admiten hasta 100 precios por cuenta.");
            watch.CounterpartStatus = "needs_review";
            watch.CounterpartNote = "La fuente asociada se eliminó durante la consulta; no se ha vuelto a crear. Puedes reintentarlo explícitamente.";
            watch.CounterpartOptOut = true;
            watch.CounterpartPending = false;
            return [];
        }
        if (existing?.SteamAppId is { } existingAppId && existingAppId != plan.AppId)
            throw Invalid("La fuente existente pertenece a otra identidad Steam; no se modificará su historial.", 409);
        if (watch.SteamAppId is { } currentAppId && currentAppId != plan.AppId)
            throw Invalid("La identidad del seguimiento cambió durante la consulta; recarga antes de asociar otra fuente.", 409);
        var adding = newOriginals + (existing is null ? 1 : 0);
        if (await db.Games.CountAsync(x => x.OwnerId == watch.OwnerId, token) + adding > 100)
            throw Invalid("Se admiten hasta 100 precios por cuenta, incluidas las fuentes asociadas.");
        watch.SteamAppId = plan.AppId;
        watch.CounterpartUrl = plan.Candidate.SourceUrl;
        if (existing is null)
        {
            db.Games.Add(plan.Candidate);
            return [plan.Candidate.Id];
        }
        existing.SteamAppId = plan.AppId;
        existing.CounterpartStatus = "verified";
        existing.CounterpartNote = plan.Note;
        existing.CounterpartPending = false;
        existing.CounterpartUrl = watch.SourceUrl;
        existing.CounterpartDiscoveryAttemptAt = clock.GetUtcNow();
        // Existing goals, histories, filters and inclusion always win. Prime/retry only missing/failed
        // quotes, and don't overwrite a newer refresh that completed during this preparation.
        var needsQuote = existing.LastSuccessAt is null || existing.Error is not null;
        var attemptUnchanged = plan.ExistingId.HasValue ? existing.LastAttemptAt == plan.ExpectedAttempt : existing.LastAttemptAt is null;
        if (needsQuote && attemptUnchanged)
        {
            var sameFilters = existing.Payment == plan.QuotedWatch!.Payment && existing.Region == plan.QuotedWatch.Region &&
                existing.OfficialOnly == plan.QuotedWatch.OfficialOnly;
            if (sameFilters)
                RefreshCoordinator.ApplyPrice(existing, plan.Prepared!.Price, plan.Prepared.Error, plan.AttemptedAt, plan.Prepared.ObservedAt ?? clock.GetUtcNow());
            else
            {
                // A different-filter source appeared only after our detached lookup. Never store the
                // wrong payment/region/merchant quote; leave it eligible for its own immediate check.
                existing.Error = "La fuente se añadió con otros filtros durante la consulta; su primera lectura queda pendiente de una comprobación propia.";
                existing.CounterpartNote = watch.CounterpartNote = plan.Note + " " + existing.Error;
            }
        }
        return [existing.Id];
    }

    private static void SetIdentity(GameWatch watch, GameResolution? resolution)
    {
        watch.CounterpartStatus = resolution?.MatchStatus ?? "needs_review";
        watch.CounterpartNote = resolution?.MatchNote ?? "La fuente no se pudo resolver. Reintenta o indica el enlace asociado para validarlo.";
        if (!GameIdentities.BaseEdition(watch.Edition))
        {
            watch.SteamAppId = null;
            watch.CounterpartStatus = "needs_review";
            watch.CounterpartNote = "Edición distinta de Standard/base: no se agrupa con la compra base de Steam.";
        }
        else
        {
            watch.SteamAppId ??= resolution?.SteamAppId;
            if (resolution?.MatchStatus == "verified") watch.CounterpartUrl = watch.Provider == "steam" ? resolution.AllKeyShopUrl : resolution.SteamUrl;
        }
    }

    private static string ValidateManualLink(GameWatch original, string url)
    {
        if (!GameIdentities.BaseEdition(original.Edition)) throw Invalid("La asociación exige la misma edición base, no un upgrade ni otra edición.", 409);
        var manual = WatchValidation.GameUrl(url) ?? throw Invalid("Usa una página pública HTTPS de Steam /app/ o de comparación de claves AllKeyShop.");
        if (manual.Provider == original.Provider) throw Invalid("El enlace asociado debe pertenecer a la otra tienda.");
        var known = GameIdentities.Find(original.SourceUrl);
        var otherKnown = GameIdentities.Find(manual.Url);
        if (known is not null && manual.Url != (original.Provider == "steam" ? known.AllKeyShopUrl : known.SteamUrl) ||
            otherKnown is not null && original.SourceUrl != (original.Provider == "steam" ? otherKnown.SteamUrl : otherKnown.AllKeyShopUrl))
            throw Invalid("El enlace entra en conflicto con el juego/SKU verificado; no se mezclarán historias ni ediciones.", 409);
        return manual.Url;
    }

    private Task<GameWatch?> DuplicateAsync(GameWatch watch, CancellationToken token)
    {
        var edition = watch.Edition.Trim().ToUpperInvariant();
        return db.Games.AsNoTracking().FirstOrDefaultAsync(x => x.OwnerId == watch.OwnerId && x.SourceUrl == watch.SourceUrl &&
            x.EditionKey == edition && x.Region == watch.Region && x.Payment == watch.Payment && x.OfficialOnly == watch.OfficialOnly, token);
    }

    private async Task<GameWatch?> ExistingSourceAsync(Guid ownerId, string url, CancellationToken token, bool tracked = false)
    {
        var query = db.Games.Where(x => x.OwnerId == ownerId && x.SourceUrl == url);
        var candidates = await (tracked ? query : query.AsNoTracking()).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token);
        // Edition text, not the legacy uniqueness discriminator. Retain every legacy duplicate untouched.
        return candidates.FirstOrDefault(x => GameIdentities.BaseEdition(x.Edition));
    }

    private async Task AssertOwnerAsync(Guid ownerId, CancellationToken token)
    {
        if (!await db.Users.AnyAsync(x => x.Id == ownerId && x.Enabled && x.PasswordHash != null, token))
            throw Invalid("La cuenta ya no tiene una sesión válida.", 401);
    }

    private async Task<GameCreationDto> WriteAsync(CancellationToken token, Func<Task<GameCreationDto>> action)
    {
        using var lease = await mutations.AcquireAsync(token);
        if (lease is null) throw Invalid("Hay otra escritura en curso; vuelve a intentarlo en unos segundos.", 409);
        try { return await action(); }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteErrorCode: 19 })
        { throw Invalid("Ya existe esa fuente o cambió durante la consulta. Recarga antes de reintentar.", 409); }
    }

    private static PairPlan NoPair(string status, string? note, bool retryable = false) => new(null, null, null, default, null, status, note, null, null, retryable);
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static GameOperationException Invalid(string message, int status = 400) => new(message, status);
    private sealed record PairPlan(GameWatch? Candidate, Guid? ExistingId, GamePreparation? Prepared, DateTimeOffset AttemptedAt,
        string? AppId, string Status, string? Note, GameWatch? QuotedWatch, DateTimeOffset? ExpectedAttempt, bool Retryable);
}
