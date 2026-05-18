using System.Threading.Channels;

namespace HookVault.Services;

public sealed class ReplayQueue
{
    // Bounded so a bulk replay of a large backlog can't balloon memory. Producers
    // await on a full channel (BoundedChannelFullMode.Wait) — backpressure surfaces
    // naturally through the existing async enqueue paths.
    private readonly Channel<ReplayJob> _channel =
        Channel.CreateBounded<ReplayJob>(new BoundedChannelOptions(10_000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public ChannelReader<ReplayJob> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(Guid eventId, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(new ReplayJob(eventId), ct);

    public ValueTask EnqueueWithBodyAsync(Guid eventId, byte[] bodyOverride, CancellationToken ct = default)
        => _channel.Writer.WriteAsync(new ReplayJob(eventId, bodyOverride), ct);

    /// <summary>
    /// Drains all pending jobs without invoking them. Test-only helper used by
    /// <c>POST /api/test/reset</c>; never called from production code paths.
    /// </summary>
    public void Drain()
    {
        while (_channel.Reader.TryRead(out _))
        {
            // discard
        }
    }
}
