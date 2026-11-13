using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Watchboard.Api.Contracts;
using Watchboard.Api.Data;
using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public sealed class OperationGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);

    // Queue short mutations instead of rejecting whenever another small write happens to be saving.
    public async Task<IDisposable?> AcquireAsync(CancellationToken token)
    {
        return await Semaphore.WaitAsync(TimeSpan.FromSeconds(3), token) ? new Lease(Semaphore) : null;
    }

    private sealed class Lease(SemaphoreSlim semaphore) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; semaphore.Release(); } }
    }
}

public sealed class RefreshCoordinator(IServiceScopeFactory scopes, OperationGate mutations, TimeProvider clock,
    IOptions<WatchboardOptions> options, ILogger<RefreshCoordinator> logger)
{
    // This gate prevents overlapping source work; it is deliberately NOT the mutation gate.
    private readonly SemaphoreSlim refreshGate = new(1, 1);

    public async Task<RefreshResult> RefreshAsync(CancellationToken token, Guid? ownerId = null, bool automatic = false,
        bool force = false, string scope = "all")
    {
        if (!automatic && ownerId is null) throw new InvalidOperationException("Manual refresh requires an owner.");
        if (scope is not ("all" or "prices" or "versions")) throw new ArgumentException("Unknown refresh scope.", nameof(scope));
        if (!await refreshGate.WaitAsync(0, token)) return new(0, 0, true, clock.GetUtcNow());
        try
        {
            List<SoftwareWatch> software;
            List<GameWatch> games;
            // Detached snapshots. No DbContext or database transaction is held during source IO.
            await using (var snapshot = scopes.CreateAsyncScope())
            {
                var db = snapshot.ServiceProvider.GetRequiredService<WatchboardDbContext>();
                var enabledOwners = db.Users.Where(x => x.Enabled && x.PasswordHash != null).Select(x => x.Id);
                software = await db.Software.AsNoTracking().Where(x => enabledOwners.Contains(x.OwnerId) &&
                    (!ownerId.HasValue || x.OwnerId == ownerId.Value)).ToListAsync(token);
                games = await db.Games.AsNoTracking().Where(x => enabledOwners.Contains(x.OwnerId) &&
                    (!ownerId.HasValue || x.OwnerId == ownerId.Value)).ToListAsync(token);
            }
            var config = options.Value;
            var now = clock.GetUtcNow();
            var checkedCount = 0;
            var succeeded = 0;
            await using var sources = scopes.CreateAsyncScope();
            var releases = sources.ServiceProvider.GetRequiredService<ReleaseSources>();
            var prices = sources.ServiceProvider.GetRequiredService<PriceSources>();
            foreach (var original in software.Where(x => scope != "prices" &&
                         (!automatic && force || Due(x.LastAttemptAt, now, automatic ? config.VersionIntervalMinutes : 5))))
            {
                checkedCount++;
                var attemptedAt = clock.GetUtcNow();
                ReleaseResult? result = null;
                string? error = null;
                try { result = await releases.ReadAsync(original, token); }
                catch (Exception ex) when (SourceFailures.Is(ex, token)) { error = SourceFailures.Message(ex); }
                using var lease = await mutations.AcquireAsync(token);
                if (lease is null) return new(checkedCount, succeeded, true, clock.GetUtcNow());
                await using var merge = scopes.CreateAsyncScope();
                var db = merge.ServiceProvider.GetRequiredService<WatchboardDbContext>();
                var watch = await db.Software.SingleOrDefaultAsync(x => x.Id == original.Id && x.OwnerId == original.OwnerId, token);
                if (watch is null || !await OwnerEnabled(db, original.OwnerId, token) ||
                    watch.Provider != original.Provider || watch.Source != original.Source) continue;
                watch.LastAttemptAt = attemptedAt;
                watch.Error = error;
                if (result is not null)
                {
                    watch.LatestVersion = result.Version;
                    watch.ReleaseUrl = result.Url;
                    watch.ReleasePublishedAt = result.PublishedAt;
                    watch.LastSuccessAt = clock.GetUtcNow();
                    succeeded++;
                }
                await db.SaveChangesAsync(token);
            }
            foreach (var original in games.Where(x => scope != "versions" && (!automatic && force || Due(x.LastAttemptAt, now, config.PriceIntervalMinutes))))
            {
                checkedCount++;
                var attemptedAt = clock.GetUtcNow();
                PriceResult? result = null;
                string? error = null;
                try { result = await prices.ReadAsync(original, token); }
                catch (Exception ex) when (SourceFailures.Is(ex, token)) { error = SourceFailures.Message(ex); }
                if (error is not null) logger.LogWarning("Price source {Provider} {SourceUrl}: {Error}", original.Provider, original.SourceUrl, error);
                using var lease = await mutations.AcquireAsync(token);
                if (lease is null) return new(checkedCount, succeeded, true, clock.GetUtcNow());
                await using var merge = scopes.CreateAsyncScope();
                var db = merge.ServiceProvider.GetRequiredService<WatchboardDbContext>();
                var watch = await db.Games.SingleOrDefaultAsync(x => x.Id == original.Id && x.OwnerId == original.OwnerId, token);
                if (watch is null || !await OwnerEnabled(db, original.OwnerId, token) || !SamePriceRequest(watch, original)) continue;
                if (result is not null && watch.Provider == "allkeyshop" && GameIdentities.BaseEdition(watch.Edition) &&
                    GameIdentities.IsDlcEdition(result.Edition))
                {
                    if (await db.Prices.AnyAsync(x => x.GameWatchId == watch.Id, token))
                    {
                        error = "AllKeyShop solo publica ahora una edición DLC. Se conserva el histórico Standard y no se mezclan las ediciones.";
                        result = null;
                    }
                    else
                    {
                        watch.Edition = result.Edition;
                        watch.CounterpartPending = !watch.CounterpartOptOut;
                        watch.CounterpartDiscoveryAttemptAt = null;
                    }
                }
                ApplyPrice(watch, result, error, attemptedAt, prices.SnapshotTime(original.SourceUrl) ?? clock.GetUtcNow());
                if (result is not null) succeeded++;
                await db.SaveChangesAsync(token);
            }
            // One-time upgrade discovery plus transport-only retries, deliberately daily unless forced.
            // Explicit opt-outs/deletions are never a re-seed rule on subsequent polling ticks.
            foreach (var original in games.Where(x => scope != "versions" && !x.CounterpartOptOut &&
                         (x.CounterpartPending || !automatic && force && x.CounterpartStatus != "verified") &&
                         (!automatic && force || Due(x.CounterpartDiscoveryAttemptAt, now, config.PriceIntervalMinutes))))
            {
                await using var repair = scopes.CreateAsyncScope();
                // Reuse the same public source snapshots already fetched for this refresh. Private DB
                // state still has a fresh scope; a second download must not undo a successful first one.
                var service = ActivatorUtilities.CreateInstance<GameWatchService>(repair.ServiceProvider, prices);
                try { await service.CounterpartAsync(original.OwnerId, original.Id, null, token, pendingOnly: automatic || !force, respectOptOut: true); }
                catch (GameOperationException exception)
                {
                    using var lease = await mutations.AcquireAsync(token);
                    if (lease is null) return new(checkedCount, succeeded, true, clock.GetUtcNow());
                    await using var merge = scopes.CreateAsyncScope();
                    var db = merge.ServiceProvider.GetRequiredService<WatchboardDbContext>();
                    var watch = await db.Games.SingleOrDefaultAsync(x => x.Id == original.Id && x.OwnerId == original.OwnerId, token);
                    if (watch is null || !await OwnerEnabled(db, original.OwnerId, token) || !SamePriceRequest(watch, original)) continue;
                    watch.CounterpartPending = false;
                    watch.CounterpartDiscoveryAttemptAt = clock.GetUtcNow();
                    watch.CounterpartStatus = "needs_review";
                    watch.CounterpartNote = "No se pudo completar la asociación: " + exception.Message + " Puedes reintentar desde el enlace asociado.";
                    await db.SaveChangesAsync(token);
                }
            }
            if (checkedCount > 0) logger.LogInformation("Source check completed: {Succeeded}/{Checked} succeeded", succeeded, checkedCount);
            return new(checkedCount, succeeded, false, clock.GetUtcNow());
        }
        finally { refreshGate.Release(); }
    }

    public async Task<RefreshResult> DetectAsync(Guid ownerId, CancellationToken token)
    {
        if (!await refreshGate.WaitAsync(0, token)) return new(0, 0, true, clock.GetUtcNow());
        try
        {
            List<SoftwareWatch> watches;
            await using (var snapshot = scopes.CreateAsyncScope())
            {
                var db = snapshot.ServiceProvider.GetRequiredService<WatchboardDbContext>();
                watches = await db.Software.AsNoTracking().Where(x => x.OwnerId == ownerId && x.LocalTool != null && x.LocationId == null).ToListAsync(token);
            }
            var succeeded = 0;
            await using var sources = scopes.CreateAsyncScope();
            var detector = sources.ServiceProvider.GetRequiredService<ILocalVersionDetector>();
            foreach (var original in watches)
            {
                var result = await detector.DetectAsync(original.LocalTool!, token);
                using var lease = await mutations.AcquireAsync(token);
                if (lease is null) return new(watches.Count, succeeded, true, clock.GetUtcNow());
                await using var merge = scopes.CreateAsyncScope();
                var db = merge.ServiceProvider.GetRequiredService<WatchboardDbContext>();
                var watch = await db.Software.SingleOrDefaultAsync(x => x.Id == original.Id && x.OwnerId == ownerId, token);
                // A manual edit made during a probe wins over the old snapshot, even at the same clock tick.
                if (watch is null || !await OwnerEnabled(db, ownerId, token) || watch.InstalledCheckedAt != original.InstalledCheckedAt ||
                    watch.InstalledVersion != original.InstalledVersion || watch.InstalledOn != original.InstalledOn || watch.InstalledMethod != original.InstalledMethod ||
                    watch.LocalTool != original.LocalTool || watch.LocationId != original.LocationId) continue;
                watch.DetectionError = result.Error;
                if (result.Version is not null)
                {
                    watch.InstalledVersion = result.Version;
                    watch.InstalledOn = Environment.MachineName;
                    watch.InstalledMethod = "detected";
                    watch.InstalledCheckedAt = clock.GetUtcNow();
                    succeeded++;
                }
                await db.SaveChangesAsync(token);
            }
            return new(watches.Count, succeeded, false, clock.GetUtcNow());
        }
        finally { refreshGate.Release(); }
    }

    public static void ApplyPrice(GameWatch watch, PriceResult? result, string? error, DateTimeOffset attempt, DateTimeOffset observedAt)
    {
        watch.LastAttemptAt = attempt;
        watch.Error = error;
        if (result is null) return;
        watch.LastSuccessAt = observedAt;
        watch.SteamAppId ??= result.SteamAppId;
        watch.Observations.Add(new PriceObservation
        {
            GameWatchId = watch.Id, ObservedAt = observedAt, PriceCents = result.PriceCents,
            Currency = result.Currency, Merchant = result.Merchant, Region = result.Region, Edition = result.Edition,
            OfferUrl = result.OfferUrl, Coupon = result.Coupon, MatchingOffers = result.MatchingOffers,
            OriginalPriceCents = result.OriginalPriceCents, DiscountPercent = result.DiscountPercent, SaleEndsAt = result.SaleEndsAt,
            PaymentMethod = result.PaymentMethod, BasePriceCents = result.BasePriceCents, FeeCents = result.FeeCents, FeeBasis = result.FeeBasis,
            Provenance = watch.Provider == "steam" ? "steam-official-es-eur" : "aks-published-pc-key-gift-payment-total-v4",
            ActivationPlatform = result.ActivationPlatform, DeliveryType = result.DeliveryType, LanguageRestriction = result.LanguageRestriction
        });
    }

    internal static bool SamePriceRequest(GameWatch current, GameWatch snapshot) => SamePriceRequestExceptEdition(current, snapshot) && current.Edition == snapshot.Edition;

    internal static bool SamePriceRequestExceptEdition(GameWatch current, GameWatch snapshot) =>
        current.Provider == snapshot.Provider && current.SourceUrl == snapshot.SourceUrl &&
        current.Region == snapshot.Region && current.Payment == snapshot.Payment && current.OfficialOnly == snapshot.OfficialOnly;

    private static Task<bool> OwnerEnabled(WatchboardDbContext db, Guid ownerId, CancellationToken token) =>
        db.Users.AnyAsync(x => x.Id == ownerId && x.Enabled && x.PasswordHash != null, token);
    private static bool Due(DateTimeOffset? attempt, DateTimeOffset now, int minutes) => attempt is null || now - attempt.Value >= TimeSpan.FromMinutes(minutes);
}
