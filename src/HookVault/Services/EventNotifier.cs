using System.Threading.Channels;

namespace HookVault.Services;

public sealed record EventNotification(Guid Id, string Provider, string Status);

public sealed class EventNotifier
{
    private readonly Channel<EventNotification> _channel =
        Channel.CreateUnbounded<EventNotification>(
            new UnboundedChannelOptions { SingleReader = false });

    public void Notify(EventNotification notification) =>
        _channel.Writer.TryWrite(notification);

    public ChannelReader<EventNotification> Reader => _channel.Reader;
}
