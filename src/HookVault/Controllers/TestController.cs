using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HookVault.Controllers;

/// <summary>
/// Test-only endpoints. Gated by two independent checks:
/// (1) the <see cref="ExcludeTestControllersConvention"/> strips this
/// controller from MVC discovery unless <c>ASPNETCORE_ENVIRONMENT=Testing</c>;
/// (2) the action body additionally requires <c>HOOKVAULT_E2E_TEST=1</c>.
/// Both must be true. A production deploy that accidentally inherits
/// <c>ASPNETCORE_ENVIRONMENT=Testing</c> still 404s because the
/// <c>HOOKVAULT_E2E_TEST</c> var is never set in production.
/// </summary>
[ApiController]
[Route("api/test")]
[AllowAnonymous]
public sealed class TestController(
    HookVaultDbContext db,
    ReplayQueue queue,
    IHostEnvironment env,
    ILogger<TestController> logger) : ControllerBase
{
    [HttpPost("reset")]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        // Double-gate: env name AND explicit opt-in env var. The env var is the
        // load-bearing one — it must never appear in any production environment.
        var optedIn = Environment.GetEnvironmentVariable("HOOKVAULT_E2E_TEST") == "1";
        if (!env.IsEnvironment("Testing") || !optedIn)
        {
            logger.LogError(
                "TestController.Reset reached without both Testing env and HOOKVAULT_E2E_TEST=1");
            return NotFound();
        }

        // EF Core resolves the entity-mapped table identifier (defaults to "Events")
        // — avoids hardcoding the table name in raw SQL.
        var tableName = db.Model.FindEntityType(typeof(Domain.WebhookEvent))?
            .GetTableName() ?? "Events";

        // Postgres and SQLite both support TRUNCATE-or-DELETE; DELETE works on both
        // and is fast enough for test-reset traffic. Avoids dialect branching.
        // tableName is sourced from EF Core's own model metadata, not user input.
#pragma warning disable EF1002
        await db.Database.ExecuteSqlRawAsync($"DELETE FROM \"{tableName}\"", ct);
#pragma warning restore EF1002
        queue.Drain();
        return NoContent();
    }
}
