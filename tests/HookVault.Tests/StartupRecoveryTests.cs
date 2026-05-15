using HookVault.Domain;
using HookVault.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace HookVault.Tests;

[Collection("EnvVarMutation")]
public sealed class StartupRecoveryTests : IAsyncLifetime
{
    private HookVaultWebApplicationFactory _baseFactory = null!;

    public async Task InitializeAsync()
    {
        _baseFactory = new HookVaultWebApplicationFactory();

        // Pre-seed a Replaying-status event into the shared in-memory DB so the
        // startup sweep has something to do.
        await using var seedDb = new HookVaultDbContext(
            new DbContextOptionsBuilder<HookVaultDbContext>()
                .UseSqlite(_baseFactory.Connection)
                .Options);

        await seedDb.Database.EnsureCreatedAsync();
        seedDb.Events.Add(new WebhookEvent
        {
            Id = Guid.NewGuid(),
            Provider = "stripe",
            Path = "/api/ingest/stripe",
            Headers = "{}",
            Body = [],
            ForwardUrl = "http://localhost/forward",
            Status = EventStatus.Replaying,
        });
        await seedDb.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _baseFactory.DisposeAsync();

    [Fact]
    public async Task Startup_TransitionsReplayingEventsToForwardFailed()
    {
        // Building the test factory triggers the startup pipeline, which should
        // sweep the seeded Replaying row.
        using var _ = _baseFactory.CreateClient();

        await using var db = new HookVaultDbContext(
            new DbContextOptionsBuilder<HookVaultDbContext>()
                .UseSqlite(_baseFactory.Connection)
                .Options);

        var statuses = await db.Events.Select(e => e.Status).ToListAsync();
        Assert.All(statuses, s => Assert.NotEqual(EventStatus.Replaying, s));
        Assert.Contains(EventStatus.ForwardFailed, statuses);
    }
}
