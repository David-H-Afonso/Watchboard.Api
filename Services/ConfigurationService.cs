using Microsoft.EntityFrameworkCore;
using Watchboard.Api.Contracts;
using Watchboard.Api.Data;
using Watchboard.Api.Models;

namespace Watchboard.Api.Services;

public sealed class ConfigValidationException(string message) : Exception(message);

public sealed class ConfigurationService(WatchboardDbContext db, TimeProvider clock)
{
    public async Task<ConfigDocument> ExportAsync(Guid ownerId, CancellationToken token)
    {
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == ownerId, token);
        var locations = await db.Locations.AsNoTracking().Where(x => x.OwnerId == ownerId).OrderBy(x => x.Name).ToListAsync(token);
        var names = locations.ToDictionary(x => x.Id, x => x.Name);
        var software = await db.Software.AsNoTracking().Where(x => x.OwnerId == ownerId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token);
        var games = await db.Games.AsNoTracking().Where(x => x.OwnerId == ownerId).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token);
        var rules = user.Preferences();
        var portable = new PortableWidgetPreferences(rules.OnlyActionable, rules.ShowVersions, rules.ShowGames, rules.ShowSales,
            rules.ShowPriceDrops, rules.ShowErrors, rules.MaxItems,
            rules.SoftwareLocationIds?.Where(names.ContainsKey).Select(id => names[id]).ToList(), rules.SoftwareUpdatesOnly);
        // Explicit projection: no entity/owner IDs, hashes, session stamps, credentials, or observations.
        return new("watchboard-config", 2, portable,
            software.Select(x => new SoftwareConfig(x.Name, x.Group, x.Provider, x.Source, x.LocalTool,
                x.InstalledVersion, x.InstalledOn, x.IncludeInWidget, x.LocationId is { } id ? names.GetValueOrDefault(id) : null)).ToList(),
            games.Select(x => new GameConfig(x.Name, x.SourceUrl, x.Edition, x.Region, x.Payment,
                x.OfficialOnly, x.TargetCents, x.IncludeInWidget, x.SteamAppId)).ToList(), clock.GetUtcNow(), "Watchboard",
            locations.Select(x => x.Name).ToList());
    }

    public async Task<ImportPreview> PreviewAsync(Guid ownerId, bool isAdmin, ConfigDocument document, CancellationToken token) =>
        (await PlanAsync(ownerId, isAdmin, document, false, token)).Preview;

    // Caller holds the short mutation gate. No source IO, and all validation precedes tracked mutations.
    public async Task<ImportPreview> ImportAsync(Guid ownerId, bool isAdmin, ConfigDocument document, CancellationToken token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var plan = await PlanAsync(ownerId, isAdmin, document, true, token);
        var locations = plan.ExistingLocations.ToDictionary(x => x.NormalizedName, StringComparer.Ordinal);
        foreach (var (name, normalized) in plan.Locations)
        {
            if (locations.ContainsKey(normalized)) continue;
            var location = new Location { OwnerId = ownerId, Name = name, NormalizedName = normalized };
            db.Locations.Add(location);
            locations.Add(normalized, location);
        }
        foreach (var (config, existing) in plan.Software)
        {
            if (existing is not null) { existing.IncludeInWidget = config.IncludeInWidget; continue; }
            var installed = Clean(config.InstalledVersion);
            db.Software.Add(new SoftwareWatch
            {
                OwnerId = ownerId, Name = config.Name, Group = config.Group, Provider = config.Provider, Source = config.Source,
                LocalTool = config.LocalTool, IncludeInWidget = config.IncludeInWidget, InstalledVersion = installed,
                LocationId = config.Location is null ? null : locations[LocationNames.Canonicalize(config.Location)!.Value.NormalizedName].Id,
                InstalledOn = installed is null ? null : Clean(config.InstalledOn) ?? "Sin especificar",
                InstalledMethod = installed is null ? null : "manual", InstalledCheckedAt = installed is null ? null : clock.GetUtcNow(),
                CreatedAt = clock.GetUtcNow()
            });
        }
        foreach (var (config, existing) in plan.Games)
        {
            if (existing is not null)
            {
                existing.TargetCents = config.TargetCents;
                existing.IncludeInWidget = config.IncludeInWidget;
                existing.SteamAppId ??= config.SteamAppId;
                continue;
            }
            var source = WatchValidation.GameUrl(config.SourceUrl)!.Value;
            var known = GameIdentities.BaseEdition(config.Edition) ? GameIdentities.Find(source.Url) : null;
            db.Games.Add(new GameWatch
            {
                OwnerId = ownerId, Name = config.Name, Provider = source.Provider, SourceUrl = source.Url, SteamAppId = source.AppId ?? config.SteamAppId,
                Edition = config.Edition, Region = config.Region, Payment = config.Payment, OfficialOnly = config.OfficialOnly,
                TargetCents = config.TargetCents, IncludeInWidget = config.IncludeInWidget, CreatedAt = clock.GetUtcNow(),
                CounterpartStatus = known is null ? "needs_review" : "verified",
                CounterpartUrl = known is null ? null : source.Provider == "steam" ? known.AllKeyShopUrl : known.SteamUrl,
                CounterpartNote = known is null ? "Fuente importada: comprueba el enlace asociado antes de vincular otra tienda." :
                    "Identidad base del catálogo verificada. Fuente importada pendiente de su primera lectura real; objetivos e historial independientes."
            });
        }
        var preferences = document.Preferences;
        var user = await db.Users.SingleAsync(x => x.Id == ownerId, token);
        user.SetPreferences(new(preferences.OnlyActionable, preferences.ShowVersions, preferences.ShowGames, preferences.ShowSales,
            preferences.ShowPriceDrops, preferences.ShowErrors, preferences.MaxItems,
            preferences.SoftwareLocations?.Select(name => locations[LocationNames.Canonicalize(name)!.Value.NormalizedName].Id).Distinct().ToList(),
            preferences.SoftwareUpdatesOnly));
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return plan.Preview;
    }

    private async Task<ImportPlan> PlanAsync(Guid ownerId, bool isAdmin, ConfigDocument document, bool tracked, CancellationToken token)
    {
        if (document.Format != "watchboard-config" || document.Version is not (1 or 2))
            throw Invalid("Se requiere format 'watchboard-config' y version 1 o 2.");
        if (document.Preferences is not { MaxItems: >= 1 and <= 30 }) throw Invalid("Las preferencias del widget necesitan maxItems entre 1 y 30.");
        if (document.ExportedBy is not null && !WatchValidation.Text(document.ExportedBy, 100)) throw Invalid("exportedBy no es válido.");
        if (document.Software is null || document.Games is null || document.Software.Count > 100 || document.Games.Count > 100)
            throw Invalid("Incluye listas software/games de como máximo 100 seguimientos cada una.");
        if (document.Locations?.Count > 100 || document.Preferences.SoftwareLocations?.Count > 100)
            throw Invalid("Se admiten hasta 100 ubicaciones por cuenta.");
        if (document.Version == 1 && (document.Locations is { Count: > 0 } || document.Preferences.SoftwareLocations is not null ||
                document.Software.Any(x => x?.Location is not null)))
            throw Invalid("Las ubicaciones portables requieren version 2.");

        var warnings = new List<string>();
        var locationNames = new Dictionary<string, string>(StringComparer.Ordinal);
        string? LocationName(string? value)
        {
            if (value is null) return null;
            var canonical = LocationNames.Canonicalize(value) ?? throw Invalid("Cada ubicación necesita un nombre de hasta 100 caracteres.");
            locationNames.TryAdd(canonical.NormalizedName, canonical.Name);
            return locationNames[canonical.NormalizedName];
        }
        foreach (var name in document.Locations ?? [])
            _ = LocationName(name ?? throw Invalid("Una ubicación no puede ser null."));
        foreach (var name in document.Preferences.SoftwareLocations ?? [])
            _ = LocationName(name ?? throw Invalid("Una ubicación del widget no puede ser null."));

        var normalizedSoftware = new List<SoftwareConfig>();
        var softwareKeys = new HashSet<string>(StringComparer.Ordinal);
        var localTools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in document.Software)
        {
            if (value is null || !WatchValidation.Text(value.Name, 100) || value.Group is not ("pc" or "server" or "project") ||
                !WatchValidation.ConfigSource(value) || !WatchValidation.Version(value.InstalledVersion) ||
                value.InstalledOn is not null && !WatchValidation.Text(value.InstalledOn, 100))
                throw Invalid("Hay una versión inválida: revisa nombre, grupo, proveedor, fuente, herramienta e instalación.");
            if (value.LocalTool is not null && !isAdmin) throw Invalid("Solo un administrador puede importar herramientas de detección local (localTool).");
            var location = LocationName(value.Location);
            var normalized = value with { Name = value.Name.Trim(), Source = WatchValidation.SoftwareSource(value.Provider, value.Source),
                InstalledVersion = Clean(value.InstalledVersion), InstalledOn = Clean(value.InstalledOn), Location = location,
                LocalTool = location is null ? value.LocalTool : null };
            if (value.LocalTool is not null && location is not null)
                warnings.Add($"{normalized.Name}: la ubicación es una clasificación manual; se desactiva la detección del host local.");
            var key = SoftwareKey(normalized.Provider, normalized.Source, normalized.LocalTool);
            if (document.Version == 1 && !softwareKeys.Add(key)) throw Invalid("El documento contiene versiones duplicadas para la misma fuente/herramienta.");
            if (normalized.LocalTool is not null && !localTools.Add(normalized.LocalTool)) throw Invalid("Cada herramienta local solo puede aparecer una vez.");
            normalizedSoftware.Add(normalized);
        }

        var normalizedGames = new List<GameConfig>();
        var gameKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in document.Games)
        {
            if (value is null || !WatchValidation.Text(value.Name, 150)) throw Invalid("Cada precio importado necesita un nombre de hasta 150 caracteres.");
            var error = WatchValidation.Game(new(value.Name, value.SourceUrl, value.Edition, value.Region, value.Payment, value.OfficialOnly, value.TargetCents));
            if (error is not null) throw Invalid(error);
            var source = WatchValidation.GameUrl(value.SourceUrl)!.Value;
            if (value.SteamAppId is not null && (value.SteamAppId.Length is < 1 or > 10 || value.SteamAppId[0] == '0' || !value.SteamAppId.All(char.IsAsciiDigit) ||
                source.Provider == "steam" && value.SteamAppId != source.AppId)) throw Invalid("steamAppId debe identificar una aplicación Steam válida y coincidir con el enlace oficial.");
            var known = GameIdentities.Find(source.Url);
            if (known is not null && value.SteamAppId is not null && value.SteamAppId != known.SteamAppId)
                throw Invalid("steamAppId entra en conflicto con la identidad verificada de esta fuente.");
            var normalized = value with { Name = value.Name.Trim(), SourceUrl = source.Url,
                Edition = source.Provider == "steam" ? "Standard" : value.Edition.Trim(), OfficialOnly = source.Provider == "steam" || value.OfficialOnly,
                Payment = source.Provider == "steam" ? "card" : value.Payment,
                SteamAppId = source.AppId ?? (GameIdentities.BaseEdition(value.Edition) ? known?.SteamAppId : null) ?? value.SteamAppId };
            if (!gameKeys.Add(GameKey(normalized.SourceUrl, normalized.Edition, normalized.Region, normalized.Payment, normalized.OfficialOnly)))
                throw Invalid("El documento contiene fuentes de precios duplicadas con los mismos filtros normalizados.");
            normalizedGames.Add(normalized);
        }
        var softwareQuery = db.Software.Where(x => x.OwnerId == ownerId);
        var gamesQuery = db.Games.Where(x => x.OwnerId == ownerId);
        var locationsQuery = db.Locations.Where(x => x.OwnerId == ownerId);
        var software = await (tracked ? softwareQuery : softwareQuery.AsNoTracking()).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token);
        var games = await (tracked ? gamesQuery : gamesQuery.AsNoTracking()).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(token);
        var locations = await (tracked ? locationsQuery : locationsQuery.AsNoTracking()).ToListAsync(token);
        if (locations.Count + locationNames.Keys.Count(key => locations.All(x => x.NormalizedName != key)) > 100)
            throw Invalid("La fusión superaría el límite de 100 ubicaciones por cuenta.");
        var savedNames = locations.ToDictionary(x => x.Id, x => x.NormalizedName);
        var used = new HashSet<Guid>();
        var softwarePlan = new List<(SoftwareConfig Config, SoftwareWatch? Existing)>();
        foreach (var value in normalizedSoftware)
        {
            var candidates = software.Where(x => !used.Contains(x.Id) &&
                SoftwareKey(x.Provider, WatchValidation.SoftwareSource(x.Provider, x.Source), x.LocalTool) == SoftwareKey(value.Provider, value.Source, value.LocalTool) &&
                (document.Version == 1 || (x.LocationId is { } id ? savedNames.GetValueOrDefault(id) : null) ==
                    (value.Location is null ? null : LocationNames.Canonicalize(value.Location)!.Value.NormalizedName))).ToList();
            // Match instances one-to-one, preferring identical installation provenance; never collapse a source's rows.
            var existing = candidates.FirstOrDefault(x => x.InstalledVersion == value.InstalledVersion && x.InstalledOn == value.InstalledOn) ?? candidates.FirstOrDefault();
            if (existing is not null) used.Add(existing.Id);
            softwarePlan.Add((value, existing));
        }
        if (software.GroupBy(x => SoftwareKey(x.Provider, WatchValidation.SoftwareSource(x.Provider, x.Source), x.LocalTool) + "\0" + x.LocationId)
                .Any(group => group.Count() > 1) || normalizedSoftware.GroupBy(x => SoftwareKey(x.Provider, x.Source, x.LocalTool) + "\0" + x.Location).Any(group => group.Count() > 1))
            warnings.Add("Hay instalaciones de la misma fuente/ubicación: se conservan todas y se emparejan una a una; los duplicados indistinguibles no se eliminan.");
        var gamePlan = normalizedGames.Select(value => (Config: value, Existing: games.FirstOrDefault(x =>
            GameKey(WatchValidation.GameUrl(x.SourceUrl)?.Url ?? x.SourceUrl, x.Edition, x.Region, x.Payment, x.OfficialOnly) ==
            GameKey(value.SourceUrl, value.Edition, value.Region, value.Payment, value.OfficialOnly)))).ToList();
        var addedSoftware = softwarePlan.Count(x => x.Existing is null);
        var addedGames = gamePlan.Count(x => x.Existing is null);
        if (software.Count + addedSoftware > 100 || games.Count + addedGames > 100) throw Invalid("La fusión superaría el límite de 100 versiones o 100 precios por cuenta.");
        foreach (var (config, existing) in softwarePlan)
        {
            if (existing is null && config.LocalTool is not null && software.Any(x => x.LocalTool == config.LocalTool))
                throw Invalid($"La herramienta {config.LocalTool} ya existe con otra fuente; no se modificará automáticamente.");
            if (existing is not null && (Clean(config.InstalledVersion) != existing.InstalledVersion || Clean(config.InstalledOn) != existing.InstalledOn))
                warnings.Add($"{config.Name}: se conserva la instalación y su procedencia existentes.");
        }
        if (softwarePlan.Any(x => x.Existing is null && x.Config.InstalledVersion is not null))
            warnings.Add("Las instalaciones de versiones nuevas se registrarán explícitamente como manuales, no como detectadas.");
        if (addedGames > 0) warnings.Add("Los precios nuevos quedan pendientes de una comprobación real; no se importan ni fabrican observaciones.");
        return new(softwarePlan, gamePlan, locations, locationNames.Select(x => (x.Value, x.Key)).ToList(),
            new(addedSoftware, softwarePlan.Count - addedSoftware, addedGames, gamePlan.Count - addedGames, warnings.Distinct().ToList()));
    }

    private static string SoftwareKey(string provider, string source, string? localTool) => $"{provider}\0{source}\0{localTool}";
    private static string GameKey(string url, string edition, string region, string payment, bool official) => $"{url}\0{edition.Trim().ToUpperInvariant()}\0{region}\0{payment}\0{official}";
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static ConfigValidationException Invalid(string message) => new(message);
    private sealed record ImportPlan(List<(SoftwareConfig Config, SoftwareWatch? Existing)> Software,
        List<(GameConfig Config, GameWatch? Existing)> Games, List<Location> ExistingLocations,
        List<(string Name, string NormalizedName)> Locations, ImportPreview Preview);
}
