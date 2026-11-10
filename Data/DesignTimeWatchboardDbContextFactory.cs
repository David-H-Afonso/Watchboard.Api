using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Watchboard.Api.Data;

// EF tooling must never start the real app, poll sources, or open the normal database.
public sealed class DesignTimeWatchboardDbContextFactory : IDesignTimeDbContextFactory<WatchboardDbContext>
{
    public WatchboardDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<WatchboardDbContext>().UseSqlite("Data Source=:memory:").Options);
}
