using System.Collections.Immutable;
using System.Threading.Channels;

namespace HookVault.Services;

public sealed record EventNotification(Guid Id, string Provider, string Status);

// Handle returned by Subscribe(). Holds the per-client channel; consumers read
// from Reader and pass the handle back to Unsubscribe() when they disconnect.
public sealed class EventSubscription
{
    internal Channel<EventNotification> Channel { get; }
    public ChannelReader<EventNotification> Reader => Channel.Reader;

    internal EventSubscription(Channel<EventNotification> channel) => Channel = channel;
}

public sealed class EventNotifier
{
    // Copy-on-write subscriber list. The Notify hot path enumerates this without
    // locking; Subscribe/Unsubscribe swap a new immutable list via Interlocked.
    private ImmutableList<EventSubscription> _subscribers = ImmutableList<EventSubscription>.Empty;

    public EventSubscription Subscribe()
    {
        // Per-client unbounded channel. Each SSE client is a single reader; the
        // notifier is the only writer to that channel.
        var ch = Channel.CreateUnbounded<EventNotification>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var subscription = new EventSubscription(ch);

        ImmutableList<EventSubscription> original, updated;
        do
        {
            original = _subscribers;
            updated = original.Add(subscription);
        }
        while (Interlocked.CompareExchange(ref _subscribers, updated, original) != original);

        return subscription;
    }

    public void Unsubscribe(EventSubscription subscription)
    {
        ImmutableList<EventSubscription> original, updated;
        do
        {
            original = _subscribers;
            updated = original.Remove(subscription);
        }
        while (Interlocked.CompareExchange(ref _subscribers, updated, original) != original);

        // Close the channel so any pending reader observes completion.
        subscription.Channel.Writer.TryComplete();
    }

    public void Notify(EventNotification notification)
    {
        var snapshot = _subscribers;
        foreach (var sub in snapshot)
            sub.Channel.Writer.TryWrite(notification);
    }
}
