using System.Threading.Channels;

namespace HookVault.Services;

public sealed class ReplayQueue
{
    private readonly Channel<ReplayJob> _channel =
        Channel.CreateUnbounded<ReplayJob>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<ReplayJob> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(Guid eventId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(new ReplayJob(eventId), ct);

    public ValueTask EnqueueWithBodyAsync(Guid eventId, byte[] bodyOverride, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(new ReplayJob(eventId, bodyOverride), ct);
}
