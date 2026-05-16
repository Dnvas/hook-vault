using HookVault.Domain;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HookVault.Tests;

public sealed class EventRetentionWorkerTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private ServiceProvider _services = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddDbContext<HookVaultDbContext>(opts => opts.UseSqlite(_connection));
        services.AddScoped<EventRepository>();
        _services = services.BuildServiceProvider();

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task RunOnce_MaxEvents5_Keeps5NewestDeletesRest()
    {
        await SeedEventsAsync(20, DateTimeOffset.UtcNow);

        var stats = new RetentionStats { MaxEvents = 5 };
        var worker = new EventRetentionWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EventRetentionWorker>.Instance,
            stats,
            maxEvents: 5,
            retention: null);

        await worker.RunOnceAsync(CancellationToken.None);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        Assert.Equal(5, await db.Events.CountAsync());
    }

    [Fact]
    public async Task RunOnce_RetentionDays1_DeletesOnlyOlder()
    {
        var now = DateTimeOffset.UtcNow;
        await SeedEventsAsync(5, now.AddDays(-2));       // older than 1 day → deleted
        await SeedEventsAsync(3, now.AddMinutes(-30));   // fresh → kept

        var stats = new RetentionStats { Retention = TimeSpan.FromDays(1) };
        var worker = new EventRetentionWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EventRetentionWorker>.Instance,
            stats,
            maxEvents: null,
            retention: TimeSpan.FromDays(1));

        await worker.RunOnceAsync(CancellationToken.None);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        Assert.Equal(3, await db.Events.CountAsync());
    }

    [Fact]
    public async Task RunOnce_NeitherSet_DoesNothing()
    {
        await SeedEventsAsync(10, DateTimeOffset.UtcNow);

        var stats = new RetentionStats();
        var worker = new EventRetentionWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EventRetentionWorker>.Instance,
            stats,
            maxEvents: null,
            retention: null);

        await worker.RunOnceAsync(CancellationToken.None);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        Assert.Equal(10, await db.Events.CountAsync());
    }

    [Fact]
    public async Task RunOnce_RecordsSweepStats()
    {
        await SeedEventsAsync(20, DateTimeOffset.UtcNow);
        var stats = new RetentionStats { MaxEvents = 5 };

        var worker = new EventRetentionWorker(
            _services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<EventRetentionWorker>.Instance,
            stats,
            maxEvents: 5,
            retention: null);

        Assert.Null(stats.LastSweepAt);

        await worker.RunOnceAsync(CancellationToken.None);

        Assert.NotNull(stats.LastSweepAt);
        Assert.Equal(15, stats.LastSweepDeleted);
    }

    private async Task SeedEventsAsync(int count, DateTimeOffset baseTime)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        for (var i = 0; i < count; i++)
        {
            db.Events.Add(new WebhookEvent
            {
                Provider = "stripe",
                Path = "/api/ingest/stripe",
                Headers = "{}",
                Body = [],
                ForwardUrl = "http://localhost/forward",
                Status = EventStatus.Received,
                ReceivedAt = baseTime.AddSeconds(i),
            });
        }
        await db.SaveChangesAsync();
    }
}
