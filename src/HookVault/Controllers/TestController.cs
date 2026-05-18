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

        // Hardcoded literal — Postgres can't parameterise table identifiers, so an
        // interpolated form would still need raw SQL. A literal is safest: no
        // surface for future code to make the table name mutable and silently
        // turn this into SQL injection. If WebhookEvent ever gets remapped to a
        // different table name, update this string at the same time.
        await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Events\"", ct);
        queue.Drain();
        return NoContent();
    }
}
