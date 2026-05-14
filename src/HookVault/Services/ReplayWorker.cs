using HookVault.Domain;
using HookVault.Infrastructure;

namespace HookVault.Services;

public sealed class ReplayWorker(
    ReplayQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<ReplayWorker> logger) : BackgroundService
{
    internal TimeSpan[] RetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var eventId in queue.Reader.ReadAllAsync(stoppingToken))
            await ProcessAsync(eventId, stoppingToken);
    }

    private async Task ProcessAsync(Guid eventId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<EventRepository>();
        var forwarder = scope.ServiceProvider.GetRequiredService<EventForwarder>();

        var evt = await repo.GetByIdAsync(eventId, ct);
        if (evt is null)
        {
            logger.LogWarning("Replay skipped: event {Id} not found", eventId);
            return;
        }

        evt.Status = EventStatus.Replaying;
        evt.ReplayCount++;
        evt.LastReplayAt = DateTimeOffset.UtcNow;
        await repo.UpdateAsync(evt, ct);

        for (var attempt = 0; attempt < RetryDelays.Length + 1; attempt++)
        {
            var result = await forwarder.SendAsync(evt, ct);

            if (result.Success)
            {
                evt.Status = EventStatus.Forwarded;
                evt.ForwardedAt = DateTimeOffset.UtcNow;
                evt.ForwardStatusCode = result.StatusCode;
                await repo.UpdateAsync(evt, ct);
                logger.LogInformation("Replay succeeded for {Id} on attempt {N}", eventId, attempt + 1);
                return;
            }

            evt.LastError = result.Error;
            evt.ForwardStatusCode = result.StatusCode;

            if (attempt < RetryDelays.Length)
            {
                logger.LogWarning("Replay attempt {N} failed for {Id}, retrying in {Delay}s",
                    attempt + 1, eventId, RetryDelays[attempt].TotalSeconds);
                await Task.Delay(RetryDelays[attempt], ct);
            }
        }

        evt.Status = EventStatus.ReplayFailed;
        await repo.UpdateAsync(evt, CancellationToken.None);
        logger.LogError("Replay exhausted all attempts for {Id}. Last error: {Error}",
            eventId, evt.LastError);
    }
}
