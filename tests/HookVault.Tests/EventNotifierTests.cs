using HookVault.Services;

namespace HookVault.Tests;

public sealed class EventNotifierTests
{
    [Fact]
    public async Task Notify_FansOutToAllSubscribers()
    {
        var notifier = new EventNotifier();

        var subA = notifier.Subscribe();
        var subB = notifier.Subscribe();
        var subC = notifier.Subscribe();

        var notification = new EventNotification(Guid.NewGuid(), "stripe", "Received");
        notifier.Notify(notification);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var a = await subA.Reader.ReadAsync(cts.Token);
        var b = await subB.Reader.ReadAsync(cts.Token);
        var c = await subC.Reader.ReadAsync(cts.Token);

        Assert.Equal(notification, a);
        Assert.Equal(notification, b);
        Assert.Equal(notification, c);
    }

    [Fact]
    public void Unsubscribe_StopsReceivingNotifications()
    {
        var notifier = new EventNotifier();

        var subA = notifier.Subscribe();
        var subB = notifier.Subscribe();

        notifier.Unsubscribe(subA);

        notifier.Notify(new EventNotification(Guid.NewGuid(), "stripe", "Received"));

        Assert.True(subB.Reader.TryRead(out _));

        // subA's channel should be drained (no items, no further writes)
        Assert.False(subA.Reader.TryRead(out _));
    }

    [Fact]
    public void Subscribe_DoesNotReceiveNotificationsFromBeforeItSubscribed()
    {
        var notifier = new EventNotifier();

        notifier.Notify(new EventNotification(Guid.NewGuid(), "stripe", "Received"));

        var sub = notifier.Subscribe();

        Assert.False(sub.Reader.TryRead(out _));
    }
}
