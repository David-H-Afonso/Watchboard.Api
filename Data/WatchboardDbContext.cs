using Microsoft.EntityFrameworkCore;
using Watchboard.Api.Models;

namespace Watchboard.Api.Data;

public sealed class WatchboardDbContext(DbContextOptions<WatchboardDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Location> Locations => Set<Location>();
    public DbSet<SoftwareWatch> Software => Set<SoftwareWatch>();
    public DbSet<GameWatch> Games => Set<GameWatch>();
    public DbSet<PriceObservation> Prices => Set<PriceObservation>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.Entity<AppUser>().HasIndex(x => x.NormalizedUsername).IsUnique();
        builder.Entity<AppUser>().Property(x => x.Username).HasMaxLength(40);
        builder.Entity<AppUser>().Property(x => x.NormalizedUsername).HasMaxLength(40);
        builder.Entity<AppUser>().Property(x => x.DisplayName).HasMaxLength(100);
        builder.Entity<AppUser>().Property(x => x.SoftwareUpdatesOnly).HasDefaultValue(true);
        builder.Entity<Location>().HasIndex(x => new { x.OwnerId, x.NormalizedName }).IsUnique();
        builder.Entity<Location>().HasAlternateKey(x => new { x.OwnerId, x.Id });
        builder.Entity<Location>().Property(x => x.Name).HasMaxLength(100);
        builder.Entity<Location>().Property(x => x.NormalizedName).HasMaxLength(200);
        builder.Entity<Location>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<SoftwareWatch>().HasOne<Location>().WithMany().HasForeignKey(x => new { x.OwnerId, x.LocationId })
            .HasPrincipalKey(x => new { x.OwnerId, x.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<SoftwareWatch>().Property(x => x.OwnerId).HasDefaultValue(DefaultAccount.AdminId);
        builder.Entity<GameWatch>().Property(x => x.OwnerId).HasDefaultValue(DefaultAccount.AdminId);
        builder.Entity<SoftwareWatch>().Property(x => x.IncludeInWidget).HasDefaultValue(true);
        builder.Entity<GameWatch>().Property(x => x.IncludeInWidget).HasDefaultValue(true);
        builder.Entity<SoftwareWatch>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<GameWatch>().HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerId).OnDelete(DeleteBehavior.Restrict);
        builder.Entity<SoftwareWatch>().HasIndex(x => new { x.OwnerId, x.LocalTool }).IsUnique();
        builder.Entity<SoftwareWatch>().Property(x => x.Name).HasMaxLength(100);
        builder.Entity<GameWatch>().Property(x => x.Name).HasMaxLength(150);
        builder.Entity<GameWatch>().HasIndex(x => new { x.OwnerId, x.SourceUrl, x.EditionKey, x.Region, x.Payment, x.OfficialOnly }).IsUnique();
        builder.Entity<GameWatch>().HasMany(x => x.Observations).WithOne()
            .HasForeignKey(x => x.GameWatchId).OnDelete(DeleteBehavior.Cascade);
        builder.Entity<PriceObservation>().HasIndex(x => new { x.GameWatchId, x.ObservedAt });
        // SQLite can persist DateTimeOffset, but cannot order/range-query it natively.
        foreach (var entity in builder.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            if (property.ClrType == typeof(DateTimeOffset))
                property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>(
                    value => value.ToUnixTimeMilliseconds(), value => DateTimeOffset.FromUnixTimeMilliseconds(value)));
            else if (property.ClrType == typeof(DateTimeOffset?))
                property.SetValueConverter(new Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset?, long?>(
                    value => value.HasValue ? value.Value.ToUnixTimeMilliseconds() : null,
                    value => value.HasValue ? DateTimeOffset.FromUnixTimeMilliseconds(value.Value) : null));
        }
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        foreach (var entry in ChangeTracker.Entries<GameWatch>().Where(x => x.State == EntityState.Added || x.Property(w => w.Edition).IsModified))
            entry.Entity.EditionKey = entry.Entity.Edition.Trim().ToUpperInvariant();
        return base.SaveChangesAsync(cancellationToken);
    }
}
