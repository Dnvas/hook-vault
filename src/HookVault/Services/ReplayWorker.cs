using HookVault.Domain;
using HookVault.Infrastructure;

namespace HookVault.Services;

public sealed class ReplayWorker(
    ReplayQueue queue,
    IServiceScopeFactory scopeFactory,
    HookVault.Observability.HookVaultMeter meter,
    ILogger<ReplayWorker> logger) : BackgroundService
{
    internal TimeSpan[] RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
            await ProcessAsync(job, stoppingToken);
    }

    private async Task ProcessAsync(ReplayJob job, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<EventRepository>();
        var forwarder = scope.ServiceProvider.GetRequiredService<EventForwarder>();

        var evt = await repo.GetByIdAsync(job.EventId, ct);
        if (evt is null)
        {
            logger.LogWarning("Replay skipped: event {Id} not found", job.EventId);
            return;
        }

        evt.Status = EventStatus.Replaying;
        evt.ReplayCount++;
        evt.LastReplayAt = DateTimeOffset.UtcNow;
        evt.LastReplayWithEditedBody = job.BodyOverride is not null;
        await repo.UpdateAsync(evt, ct);

        var bodyToSend = job.BodyOverride ?? evt.Body;

        for (var attempt = 0; attempt < RetryDelays.Length + 1; attempt++)
        {
            var result = await forwarder.SendAsync(evt, bodyToSend, ct);

            if (result.Success)
            {
                evt.Status = EventStatus.Forwarded;
                evt.ForwardedAt = DateTimeOffset.UtcNow;
                evt.ForwardStatusCode = result.StatusCode;
                await repo.UpdateAsync(evt, ct);
                meter.ReplaysTotal.Add(1,
                    new KeyValuePair<string, object?>("outcome", "success"));
                logger.LogInformation("Replay succeeded for {Id} on attempt {N}", job.EventId, attempt + 1);
                return;
            }

            evt.LastError = result.Error;
            evt.ForwardStatusCode = result.StatusCode;

            // 4xx responses (except 408, 425, 429) signal a configuration or auth
            // error, not a transient failure. Burning retries on them just delays
            // the inevitable ReplayFailed and pins worker slots.
            if (!IsRetriable(result.StatusCode))
            {
                logger.LogWarning(
                    "Replay attempt {N} for {Id} returned non-retriable status {Status}; skipping retries.",
                    attempt + 1, job.EventId, result.StatusCode);
                break;
            }

            if (attempt < RetryDelays.Length)
            {
                meter.ReplaysTotal.Add(1,
                    new KeyValuePair<string, object?>("outcome", "retry"));
                logger.LogWarning("Replay attempt {N} failed for {Id}, retrying in {Delay}s",
                    attempt + 1, job.EventId, RetryDelays[attempt].TotalSeconds);
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        evt.Status = EventStatus.ReplayFailed;
        await repo.UpdateAsync(evt, CancellationToken.None);
        meter.ReplaysTotal.Add(1,
            new KeyValuePair<string, object?>("outcome", "exhausted"));
        logger.LogError("Replay exhausted all attempts for {Id}. Last error: {Error}",
            job.EventId, evt.LastError);
    }

    // 5xx, network errors, and timeouts are retriable. 4xx is not — except 408
    // (Request Timeout), 425 (Too Early), and 429 (Too Many Requests) which are
    // transient by definition.
    internal static bool IsRetriable(int? statusCode)
    {
        if (statusCode is null) return true;
        var code = statusCode.Value;
        if (code >= 500) return true;
        if (code is 408 or 425 or 429) return true;
        if (code >= 400) return false;
        return true;
    }
}
