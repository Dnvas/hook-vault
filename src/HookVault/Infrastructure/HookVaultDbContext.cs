using HookVault.Domain;
using Microsoft.EntityFrameworkCore;

namespace HookVault.Infrastructure;

// DbContext is the EF Core equivalent of Django's ORM — it tracks entities
// and translates LINQ queries into SQL. Each request gets its own scoped instance.
public class HookVaultDbContext(DbContextOptions<HookVaultDbContext> options) : DbContext(options)
{
    public DbSet<WebhookEvent> Events => Set<WebhookEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WebhookEvent>(entity =>
        {
            entity.HasIndex(e => e.Provider);
            entity.HasIndex(e => e.Status);
            entity.HasIndex(e => e.ReceivedAt);

            // Store enum as string so the DB is human-readable (not magic ints)
            entity.Property(e => e.Status)
                .HasConversion<string>();
        });
    }
}
