using HookVault.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HookVault.Infrastructure;

// DbContext is the EF Core equivalent of Django's ORM — it tracks entities
// and translates LINQ queries into SQL. Each request gets its own scoped instance.
public class HookVaultDbContext(DbContextOptions<HookVaultDbContext> options) : DbContext(options)
{
    public DbSet<WebhookEvent> Events => Set<WebhookEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // SQLite cannot ORDER BY DateTimeOffset columns natively — store as UTC ticks (long)
        // so sorting and range filtering translate to simple integer comparisons.
        var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, long>(
            v => v.UtcTicks,
            v => new DateTimeOffset(v, TimeSpan.Zero));

        var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, long?>(
            v => v == null ? null : v.Value.UtcTicks,
            v => v == null ? null : new DateTimeOffset(v.Value, TimeSpan.Zero));

        modelBuilder.Entity<WebhookEvent>(entity =>
        {
            entity.HasIndex(e => e.Provider);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ReceivedAt);

            // Store enum as string so the DB is human-readable (not magic ints)
            entity.Property(e => e.Status)
                .HasConversion<string>();

            entity.Property(e => e.ReceivedAt).HasConversion(dateTimeOffsetConverter);
            entity.Property(e => e.ForwardedAt).HasConversion(nullableDateTimeOffsetConverter);
            entity.Property(e => e.LastReplayAt).HasConversion(nullableDateTimeOffsetConverter);
        });
    }
}
