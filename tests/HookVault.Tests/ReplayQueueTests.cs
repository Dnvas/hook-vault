using HookVault.Services;

namespace HookVault.Tests;

public sealed class ReplayQueueTests
{
    [Fact]
    public async Task Drain_RemovesAllPendingItems()
    {
        var queue = new ReplayQueue();
        await queue.EnqueueAsync(Guid.NewGuid());
        await queue.EnqueueAsync(Guid.NewGuid());
        await queue.EnqueueAsync(Guid.NewGuid());

        queue.Drain();

        // After drain the reader should report no items waiting.
        Assert.Equal(0, queue.Reader.Count);
    }

    [Fact]
    public void Drain_OnEmptyQueue_IsNoOp()
    {
        var queue = new ReplayQueue();
        var ex = Record.Exception(() => queue.Drain());
        Assert.Null(ex);
        Assert.Equal(0, queue.Reader.Count);
    }
}
