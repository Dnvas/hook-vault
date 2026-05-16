using HookVault.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace HookVault.Services;

// Background service that prunes captured events to a configured cap (count and/or age).
// Both bounds are optional via env vars:
//   HOOKVAULT_MAX_EVENTS         — keep at most N newest events (by ReceivedAt)
//   HOOKVAULT_RETENTION_DAYS     — delete events older than N days
//   HOOKVAULT_RETENTION_INTERVAL_SECONDS — sweep cadence (default 300)
public sealed class EventRetentionWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<EventRetentionWorker> _logger;
    private readonly RetentionStats _stats;
    private readonly int? _maxEvents;
    private readonly TimeSpan? _retention;
    private readonly TimeSpan _interval;

    public EventRetentionWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<EventRetentionWorker> logger,
        RetentionStats stats,
        int? maxEvents,
        TimeSpan? retention,
        TimeSpan? interval = null)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _stats = stats;
        _maxEvents = maxEvents;
        _retention = retention;
        _interval = interval ?? TimeSpan.FromSeconds(300);
    }

    public static EventRetentionWorker FromEnvironment(
        IServiceScopeFactory scopeFactory,
        ILogger<EventRetentionWorker> logger,
        RetentionStats stats)
    {
        var maxEvents = TryParseInt(Environment.GetEnvironmentVariable("HOOKVAULT_MAX_EVENTS"));
        var days = TryParseInt(Environment.GetEnvironmentVariable("HOOKVAULT_RETENTION_DAYS"));
        var retention = days is { } d ? TimeSpan.FromDays(d) : (TimeSpan?)null;
        var intervalSeconds = TryParseInt(Environment.GetEnvironmentVariable("HOOKVAULT_RETENTION_INTERVAL_SECONDS"));
        var interval = intervalSeconds is { } s ? TimeSpan.FromSeconds(s) : (TimeSpan?)null;
        return new EventRetentionWorker(scopeFactory, logger, stats, maxEvents, retention, interval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_maxEvents is null && _retention is null)
        {
            _logger.LogInformation(
                "EventRetentionWorker idle: neither HOOKVAULT_MAX_EVENTS nor HOOKVAULT_RETENTION_DAYS is set.");
            return;
        }

        _logger.LogInformation(
            "EventRetentionWorker started (maxEvents={Max}, retention={Retention}, interval={Interval}s)",
            _maxEvents, _retention, _interval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "EventRetentionWorker sweep failed; retrying next tick.");
            }

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HookVaultDbContext>();
        var totalDeleted = 0;

        if (_retention is { } window)
        {
            var cutoff = DateTimeOffset.UtcNow - window;
            var deleted = await db.Events
                .Where(e => e.ReceivedAt < cutoff)
                .ExecuteDeleteAsync(ct);
            if (deleted > 0)
            {
                _logger.LogWarning("Retention sweep deleted {Count} events older than {Days}d.", deleted, window.TotalDays);
                totalDeleted += deleted;
            }
        }

        if (_maxEvents is { } max)
        {
            var excess = await db.Events.CountAsync(ct) - max;
            if (excess > 0)
            {
                var idsToDelete = await db.Events
                    .OrderBy(e => e.ReceivedAt)
                    .Take(excess)
                    .Select(e => e.Id)
                    .ToListAsync(ct);

                var deleted = await db.Events
                    .Where(e => idsToDelete.Contains(e.Id))
                    .ExecuteDeleteAsync(ct);
                if (deleted > 0)
                {
                    _logger.LogWarning("Retention sweep deleted {Count} oldest events past cap {Cap}.", deleted, max);
                    totalDeleted += deleted;
                }
            }
        }

        _stats.RecordSweep(totalDeleted);
    }

    private static int? TryParseInt(string? s) =>
        int.TryParse(s, out var n) && n > 0 ? n : null;
}
