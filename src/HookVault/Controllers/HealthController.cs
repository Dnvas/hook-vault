using HookVault.Configuration;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HookVault.Controllers;

[AllowAnonymous]
[ApiController]
public class HealthController(
    HookVaultOptions options,
    EventRepository repo,
    RetentionStats retention) : ControllerBase
{
    private static readonly string Version =
        typeof(HealthController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    [HttpGet("api/health")]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var dbKind = Environment.GetEnvironmentVariable("DATABASE_URL") is not null
            ? "postgresql"
            : "sqlite";

        var count = await repo.CountAsync(ct);
        var oldest = await repo.OldestEventAtAsync(ct);

        return Ok(new
        {
            status = "healthy",
            version = Version,
            providers = options.Providers.Select(p => p.Name).ToArray(),
            database = dbKind,
            eventCount = count,
            oldestEvent = oldest,
            retention = retention.MaxEvents is null && retention.Retention is null
                ? null
                : (object)new
                {
                    maxEvents = retention.MaxEvents,
                    retentionDays = retention.Retention?.TotalDays,
                    lastSweepAt = retention.LastSweepAt,
                    lastSweepDeleted = retention.LastSweepDeleted,
                },
        });
    }
}
