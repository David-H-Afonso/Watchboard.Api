using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Watchboard.Api.Contracts;
using Watchboard.Api.Data;
using Watchboard.Api.Models;
using Watchboard.Api.Services;
using static Watchboard.Api.Endpoints.EndpointSupport;

namespace Watchboard.Api.Endpoints;

public static class WatchboardEndpoints
{
    public static void MapWatchboard(this WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new { status = "ok", application = "Watchboard", version = typeof(WatchboardEndpoints).Assembly.GetName().Version!.ToString(3) }));
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/dashboard", async (ClaimsPrincipal user, DashboardReader reader, CancellationToken token) => Results.Ok(await reader.ReadAsync(user.UserId(), token)));
        api.MapGet("/widget", async (ClaimsPrincipal user, DashboardReader reader, CancellationToken token) => Results.Ok(await reader.WidgetAsync(user.UserId(), token)));
        api.MapPost("/refresh", async (HttpRequest request, ClaimsPrincipal user, RefreshCoordinator coordinator, CancellationToken token) =>
        {
            var value = new RefreshRequest();
            if (request.ContentLength is > 0 || request.Headers.ContainsKey("Transfer-Encoding"))
            {
                if (!request.HasJsonContentType()) return Problem("Usa application/json para las opciones de comprobación.", 415);
                try { value = await request.ReadFromJsonAsync<RefreshRequest>(token); }
                catch (JsonException) { return Problem("Las opciones de comprobación no son JSON válido."); }
                if (value is null) return Problem("Las opciones de comprobación no pueden ser null.");
            }
            if (value.Scope is not ("all" or "prices" or "versions")) return Problem("El ámbito debe ser all, prices o versions.");
            var result = await coordinator.RefreshAsync(token, user.UserId(), force: value.Force, scope: value.Scope);
            return result.Busy ? Problem("Ya hay una comprobación de fuentes en curso. Puedes seguir añadiendo o eliminando seguimientos.", 409) : Results.Ok(result);
        }).RequireRateLimiting("writes");
        api.MapPost("/software/detect", async (ClaimsPrincipal user, RefreshCoordinator coordinator, CancellationToken token) =>
        {
            var result = await coordinator.DetectAsync(user.UserId(), token);
            return result.Busy ? Problem("Ya hay una comprobación en curso. Puedes seguir editando los seguimientos.", 409) : Results.Ok(result);
        }).RequireAuthorization(UserAccounts.AdminPolicy).RequireRateLimiting("writes");

        api.MapGet("/locations", async (ClaimsPrincipal user, WatchboardDbContext db, CancellationToken token) => Results.Ok(
            await db.Locations.AsNoTracking().Where(x => x.OwnerId == user.UserId()).OrderBy(x => x.Name)
                .Select(x => new LocationDto(x.Id, x.Name)).ToListAsync(token)));
        api.MapPost("/locations", async (CreateLocation value, ClaimsPrincipal user, WatchboardDbContext db,
            OperationGate gate, CancellationToken token) =>
        {
            var canonical = LocationNames.Canonicalize(value.Name);
            if (canonical is null) return Problem("Escribe una ubicación de hasta 100 caracteres.");
            return await Mutate(gate, token, async () =>
            {
                var ownerId = user.UserId();
                var key = canonical.Value.NormalizedName;
                var existing = await db.Locations.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerId == ownerId && x.NormalizedName == key, token);
                if (existing is not null) return Results.Ok(new LocationDto(existing.Id, existing.Name));
                if (await db.Locations.CountAsync(x => x.OwnerId == ownerId, token) >= 100) return Problem("Se admiten hasta 100 ubicaciones por cuenta.");
                var location = new Location { OwnerId = ownerId, Name = canonical.Value.Name, NormalizedName = key };
                db.Locations.Add(location);
                await db.SaveChangesAsync(token);
                return Results.Created($"/api/locations/{location.Id}", new LocationDto(location.Id, location.Name));
            });
        }).RequireRateLimiting("writes");

        api.MapPost("/software", async (CreateSoftware value, ClaimsPrincipal user, WatchboardDbContext db,
            OperationGate gate, TimeProvider clock, CancellationToken token) =>
        {
            var error = WatchValidation.Software(value);
            if (error is not null) return Problem(error);
            return await Mutate(gate, token, async () =>
            {
                var ownerId = user.UserId();
                if (!await OwnedLocation(db, ownerId, value.LocationId, token)) return Results.NotFound();
                if (await db.Software.CountAsync(x => x.OwnerId == ownerId, token) >= 100) return Problem("Se admiten hasta 100 versiones por cuenta.");
                var installed = Clean(value.InstalledVersion);
                var watch = new SoftwareWatch
                {
                    OwnerId = ownerId, Name = value.Name.Trim(), Group = value.Group, Provider = value.Provider,
                    Source = WatchValidation.SoftwareSource(value.Provider, value.Source), InstalledVersion = installed, LocationId = value.LocationId,
                    InstalledOn = installed is null ? null : "Sin especificar", InstalledMethod = installed is null ? null : "manual",
                    InstalledCheckedAt = installed is null ? null : clock.GetUtcNow(), CreatedAt = clock.GetUtcNow()
                };
                db.Software.Add(watch);
                await db.SaveChangesAsync(token);
                return Results.Created($"/api/software/{watch.Id}", new { watch.Id });
            });
        }).RequireRateLimiting("writes");

        api.MapPut("/software/{id:guid}", async (Guid id, UpdateSoftware value, ClaimsPrincipal user,
            WatchboardDbContext db, OperationGate gate, TimeProvider clock, CancellationToken token) => await Mutate(gate, token, async () =>
        {
            var watch = await db.Software.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == user.UserId(), token);
            if (watch is null) return Results.NotFound();
            if (!WatchValidation.Text(value.Name, 100) || value.Group is not ("pc" or "server" or "project") ||
                !WatchValidation.ConfigSource(new SoftwareConfig(value.Name, value.Group, value.Provider, value.Source, null,
                    value.InstalledVersion, value.InstalledOn, watch.IncludeInWidget)) ||
                !WatchValidation.Version(value.InstalledVersion) || value.InstalledOn is not null && !WatchValidation.Text(value.InstalledOn, 100))
                return Problem("Revisa el nombre, categoría, proveedor, fuente y datos de instalación.");
            if (!await OwnedLocation(db, user.UserId(), value.LocationId, token)) return Results.NotFound();
            var source = WatchValidation.SoftwareSource(value.Provider, value.Source);
            var changedSource = watch.Provider != value.Provider || WatchValidation.SoftwareSource(watch.Provider, watch.Source) != source;
            var changedLocation = watch.LocationId != value.LocationId;
            var installed = Clean(value.InstalledVersion);
            var installedOn = Clean(value.InstalledOn);
            var changedInstallation = watch.InstalledVersion != installed || watch.InstalledOn != installedOn;
            watch.Name = value.Name.Trim();
            watch.Group = value.Group;
            watch.Provider = value.Provider;
            watch.Source = source;
            watch.LocationId = value.LocationId;
            if (changedSource || changedLocation || changedInstallation) watch.LocalTool = null;
            if (changedSource)
            {
                watch.LatestVersion = null;
                watch.ReleaseUrl = null;
                watch.ReleasePublishedAt = null;
                watch.LastAttemptAt = null;
                watch.LastSuccessAt = null;
                watch.Error = null;
                watch.DetectionError = null;
            }
            if (changedInstallation)
            {
                watch.InstalledVersion = installed;
                watch.InstalledOn = installed is null ? null : installedOn ?? "Sin especificar";
                watch.InstalledMethod = installed is null ? null : "manual";
                watch.InstalledCheckedAt = installed is null ? null : clock.GetUtcNow();
                watch.DetectionError = null;
            }
            await db.SaveChangesAsync(token);
            return Results.NoContent();
        })).RequireRateLimiting("writes");

        api.MapPatch("/software/{id:guid}/installed", async (Guid id, SetInstalledVersion value, ClaimsPrincipal user,
            WatchboardDbContext db, OperationGate gate, TimeProvider clock, CancellationToken token) => await Mutate(gate, token, async () =>
        {
            var watch = await db.Software.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == user.UserId(), token);
            if (watch is null) return Results.NotFound();
            if (!WatchValidation.Version(value.InstalledVersion) || value.InstalledOn is not null && !WatchValidation.Text(value.InstalledOn, 100))
                return Problem("Revisa la versión y el nombre del equipo (máximo 100 caracteres).");
            if (value.UpdateLocation && !await OwnedLocation(db, user.UserId(), value.LocationId, token)) return Results.NotFound();
            if (value.UpdateLocation) watch.LocationId = value.LocationId;
            // A manual installation is not an instruction to probe this host (nor any remote location).
            watch.LocalTool = null;
            watch.InstalledVersion = Clean(value.InstalledVersion);
            watch.InstalledOn = watch.InstalledVersion is null ? null : Clean(value.InstalledOn) ?? "Sin especificar";
            watch.InstalledMethod = watch.InstalledVersion is null ? null : "manual";
            watch.InstalledCheckedAt = watch.InstalledVersion is null ? null : clock.GetUtcNow();
            watch.DetectionError = null;
            await db.SaveChangesAsync(token);
            return Results.NoContent();
        })).RequireRateLimiting("writes");

        api.MapPatch("/software/{id:guid}/location", async (Guid id, SetSoftwareLocation value, ClaimsPrincipal user,
            WatchboardDbContext db, OperationGate gate, CancellationToken token) => await Mutate(gate, token, async () =>
        {
            var watch = await db.Software.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == user.UserId(), token);
            if (watch is null || !await OwnedLocation(db, user.UserId(), value.LocationId, token)) return Results.NotFound();
            if (watch.LocationId != value.LocationId)
            {
                watch.LocationId = value.LocationId;
                watch.LocalTool = null; // No proof that the chosen location denotes the API's own host.
            }
            // Classification alone never rewrites installedOn/method/time or fabricates a detection.
            await db.SaveChangesAsync(token);
            return Results.NoContent();
        })).RequireRateLimiting("writes");

        api.MapPatch("/software/{id:guid}/widget", async (Guid id, SetWidgetInclusion value, ClaimsPrincipal user,
            WatchboardDbContext db, OperationGate gate, CancellationToken token) => await Mutate(gate, token, async () =>
        {
            var watch = await db.Software.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == user.UserId(), token);
            if (watch is null) return Results.NotFound();
            watch.IncludeInWidget = value.Included;
            await db.SaveChangesAsync(token);
            return Results.NoContent();
        })).RequireRateLimiting("writes");

        api.MapDelete("/software/{id:guid}", async (Guid id, ClaimsPrincipal user, WatchboardDbContext db, OperationGate gate, CancellationToken token) =>
            await Mutate(gate, token, async () => await db.Software.Where(x => x.Id == id && x.OwnerId == user.UserId()).ExecuteDeleteAsync(token) > 0
                ? Results.NoContent() : Results.NotFound())).RequireRateLimiting("writes");

        api.MapPost("/games/resolve", async (ResolveGame value, PriceSources prices, CancellationToken token) =>
        {
            if (WatchValidation.GameUrl(value.SourceUrl) is null) return Problem("Usa una página pública HTTPS de AllKeyShop o Steam.");
            try { return Results.Ok(await prices.ResolveAsync(value.SourceUrl, token)); }
            catch (Exception ex) when (SourceFailures.Is(ex, token)) { return Problem(SourceFailures.Message(ex), 502); }
        }).RequireRateLimiting("writes");

        api.MapPost("/games", async (CreateGame value, ClaimsPrincipal user, GameWatchService games, CancellationToken token) =>
        {
            try
            {
                var result = await games.CreateAsync(user.UserId(), value, token);
                return Results.Created($"/api/games/{result.Id}", result);
            }
            catch (GameOperationException exception) { return Problem(exception.Message, exception.StatusCode); }
        }).RequireRateLimiting("writes");

        api.MapPut("/games/{id:guid}", async (Guid id, UpdateGame value, ClaimsPrincipal user,
            GameWatchService games, CancellationToken token) =>
        {
            try { return Results.Ok(await games.UpdateAsync(user.UserId(), id, value, token)); }
            catch (GameOperationException exception) { return Problem(exception.Message, exception.StatusCode); }
        }).RequireRateLimiting("writes");

        api.MapPost("/games/{id:guid}/counterpart", async (Guid id, LinkCounterpart value, ClaimsPrincipal user,
            GameWatchService games, CancellationToken token) =>
        {
            try { return Results.Ok(await games.CounterpartAsync(user.UserId(), id, value.SourceUrl, token)); }
            catch (GameOperationException exception) { return Problem(exception.Message, exception.StatusCode); }
        }).RequireRateLimiting("writes");

        api.MapPatch("/games/{id:guid}/target", async (Guid id, SetPriceTarget value, ClaimsPrincipal user,
            WatchboardDbContext db, OperationGate gate, CancellationToken token) => await Mutate(gate, token, async () =>
        {
            var watch = await db.Games.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == user.UserId(), token);
            if (watch is null) return Results.NotFound();
            if (!WatchValidation.Target(value.TargetCents)) return Problem("El objetivo debe estar entre 0 y 10.000 €.");
            watch.TargetCents = value.TargetCents;
            await db.SaveChangesAsync(token);
            return Results.NoContent();
        })).RequireRateLimiting("writes");

        api.MapPatch("/games/{id:guid}/widget", async (Guid id, SetWidgetInclusion value, ClaimsPrincipal user,
            WatchboardDbContext db, OperationGate gate, CancellationToken token) => await Mutate(gate, token, async () =>
        {
            var watch = await db.Games.SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == user.UserId(), token);
            if (watch is null) return Results.NotFound();
            watch.IncludeInWidget = value.Included;
            await db.SaveChangesAsync(token);
            return Results.NoContent();
        })).RequireRateLimiting("writes");

        api.MapDelete("/games/{id:guid}", async (Guid id, ClaimsPrincipal user, WatchboardDbContext db, OperationGate gate,
            TimeProvider clock, CancellationToken token) => await Mutate(gate, token, async () =>
        {
            var ownerId = user.UserId();
            var watch = await db.Games.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.OwnerId == ownerId, token);
            if (watch is null) return Results.NotFound();
            await using var transaction = await db.Database.BeginTransactionAsync(token);
            var related = await db.Games.Where(x => x.OwnerId == ownerId && x.Id != id &&
                (x.CounterpartUrl == watch.SourceUrl || watch.SteamAppId != null && x.SteamAppId == watch.SteamAppId &&
                    x.Provider != watch.Provider && x.CounterpartStatus == "verified")).ToListAsync(token);
            foreach (var other in related)
            {
                other.CounterpartOptOut = true;
                other.CounterpartPending = false;
                other.CounterpartDiscoveryAttemptAt = clock.GetUtcNow();
                other.CounterpartNote = "La fuente asociada fue eliminada explícitamente. Solo un reintento explícito puede volver a añadirla.";
            }
            await db.SaveChangesAsync(token);
            await db.Games.Where(x => x.Id == id && x.OwnerId == ownerId).ExecuteDeleteAsync(token);
            await transaction.CommitAsync(token);
            return Results.NoContent();
        })).RequireRateLimiting("writes");

        api.MapGet("/preferences", async (ClaimsPrincipal user, WatchboardDbContext db, CancellationToken token) => Results.Ok(
            (await db.Users.AsNoTracking().SingleAsync(x => x.Id == user.UserId(), token)).Preferences()));
        api.MapPut("/preferences", async (WidgetPreferences value, ClaimsPrincipal principal, WatchboardDbContext db,
            OperationGate gate, CancellationToken token) =>
        {
            if (!WatchValidation.Preferences(value)) return Problem("El widget admite entre 1 y 30 elementos y hasta 100 ubicaciones distintas y válidas.");
            return await Mutate(gate, token, async () =>
            {
                var user = await db.Users.SingleAsync(x => x.Id == principal.UserId(), token);
                if (value.SoftwareLocationIds is { } ids && await db.Locations.CountAsync(x => x.OwnerId == user.Id && ids.Contains(x.Id), token) != ids.Count)
                    return Results.NotFound();
                user.SetPreferences(value);
                await db.SaveChangesAsync(token);
                return Results.Ok(user.Preferences());
            });
        }).RequireRateLimiting("writes");
    }

    private static async Task<bool> OwnedLocation(WatchboardDbContext db, Guid ownerId, Guid? id, CancellationToken token) =>
        id is null || await db.Locations.AnyAsync(x => x.Id == id && x.OwnerId == ownerId, token);
}
