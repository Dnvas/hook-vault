using System.Threading.Channels;

namespace HookVault.Services;

public sealed class ReplayQueue
{
    private readonly Channel<Guid> _channel =
        Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(Guid eventId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(eventId, ct);
}
