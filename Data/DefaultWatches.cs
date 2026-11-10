using Microsoft.EntityFrameworkCore;
using Watchboard.Api.Models;
using Watchboard.Api.Services;

namespace Watchboard.Api.Data;

public static class DefaultWatches
{
    // Called only for a newly-created database: deleted watches never reappear on restart.
    public static async Task SeedAsync(WatchboardDbContext db, CancellationToken token)
    {
        if (await db.Software.AnyAsync(token) || await db.Games.AnyAsync(token)) return;
        db.Software.AddRange(
            new SoftwareWatch { Name = "Node.js", Provider = "node-lts", Source = "lts", LocalTool = "node" },
            new SoftwareWatch { Name = "Git", Provider = OperatingSystem.IsWindows() ? "github" : "github-tags", Source = OperatingSystem.IsWindows() ? "git-for-windows/git" : "git/git", LocalTool = "git" },
            new SoftwareWatch { Name = "OpenCode", Provider = "github", Source = "anomalyco/opencode", LocalTool = "opencode" },
            new SoftwareWatch { Name = ".NET SDK", Provider = "dotnet", Source = "10.0", LocalTool = "dotnet" },
            new SoftwareWatch { Name = "Jellyfin", Group = "server", Provider = "github", Source = "jellyfin/jellyfin" },
            new SoftwareWatch { Name = "Sonarr", Group = "server", Provider = "github", Source = "Sonarr/Sonarr" }
        );
        db.Games.Add(new GameWatch
        {
            Name = "Yakuza 6: The Song of Life",
            SteamAppId = "1388590",
            CounterpartStatus = "verified",
            CounterpartNote = "Identidad base verificada; la compra oficial mantiene un objetivo e historial independientes.",
            SourceUrl = "https://www.allkeyshop.com/blog/buy-yakuza-6-the-song-of-life-cd-key-compare-prices/"
        });
        await db.SaveChangesAsync(token);
    }

    // Persisted one-time addition, also for upgraded v1 databases. Deleted watches do not reappear.
    public static async Task EnsureV2Async(WatchboardDbContext db, CancellationToken token)
    {
        var admin = await db.Users.SingleAsync(x => x.Id == DefaultAccount.AdminId, token);
        if (admin.RequestedWatchesSeeded) return;
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var software = await db.Software.Where(x => x.OwnerId == admin.Id).ToListAsync(token);
        var pokemon = software.FirstOrDefault(x => x.Provider == "github" &&
            WatchValidation.RepositorySource(x.Source) == "mamesj/pokemon-fulldex");
        if (pokemon is null) db.Software.Add(new SoftwareWatch { OwnerId = admin.Id, Name = "Pokémon Fulldex", Group = "project",
            Provider = "github", Source = "mamesj/pokemon-fulldex" }); // Installed version intentionally unknown.
        else pokemon.Source = "mamesj/pokemon-fulldex";
        var games = await db.Games.Where(x => x.OwnerId == admin.Id).ToListAsync(token);
        foreach (var legacy in games.Where(x => x.Provider == "allkeyshop" && x.SourceUrl ==
                     "https://www.allkeyshop.com/blog/buy-yakuza-6-the-song-of-life-cd-key-compare-prices/" && GameIdentities.BaseEdition(x.Edition)))
        {
            legacy.SteamAppId = "1388590";
            legacy.CounterpartStatus = "verified";
        }
        if (!games.Any(x => WatchValidation.GameUrl(x.SourceUrl)?.AppId == "1388590"))
            db.Games.Add(new GameWatch { OwnerId = admin.Id, Name = "Yakuza 6: The Song of Life", Provider = "steam",
                SourceUrl = "https://store.steampowered.com/app/1388590/", SteamAppId = "1388590", Payment = "card", OfficialOnly = true,
                CounterpartStatus = "verified", CounterpartNote = "Identidad base verificada; objetivo e historial independientes de las claves." });
        admin.RequestedWatchesSeeded = true;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
    }
}
