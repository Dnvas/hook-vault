using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HookVault.Controllers;

/// <summary>
/// Test-only endpoints. Only registered when
/// <see cref="IHostEnvironment.EnvironmentName"/> is "Testing".
/// Outside Testing the route does not exist (404). The action body re-checks
/// the environment as belt-and-braces against accidental registration.
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
        if (!env.IsEnvironment("Testing"))
        {
            // Should be unreachable: TestController is only added to MVC parts
            // when env.IsEnvironment("Testing"). Defence-in-depth.
            logger.LogError("TestController.Reset reached outside Testing env");
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
