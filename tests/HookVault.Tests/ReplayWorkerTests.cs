using System.Net;
using HookVault.Domain;
using HookVault.Infrastructure;
using HookVault.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace HookVault.Tests;

public sealed class ReplayWorkerTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly HookVaultDbContext _db;
    private readonly EventRepository _repo;
    private readonly List<ServiceProvider> _providers = [];

    public ReplayWorkerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<HookVaultDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new HookVaultDbContext(options);
        _db.Database.EnsureCreated();
        _repo = new EventRepository(_db);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var p in _providers)
            await p.DisposeAsync();
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private (ReplayWorker worker, ReplayQueue queue, SequencedHandler handler) BuildWorker(
        params HttpStatusCode[] responses)
    {
        var handler = new SequencedHandler(responses);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<HookVaultDbContext>(opts => opts.UseSqlite(_connection));
        services.AddScoped<EventRepository>();
        services.AddScoped<EventForwarder>();
        services.AddHttpClient("forwarder")
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var queue = new ReplayQueue();
        var worker = new ReplayWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ReplayWorker>.Instance)
        {
            RetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]
        };

        return (worker, queue, handler);
    }

    private static WebhookEvent MakeEvent() => new()
    {
        Provider = "test",
        Path = "/test",
        Headers = "{}",
        Body = "hello",
        ForwardUrl = "http://localhost/webhook",
        Status = EventStatus.ForwardFailed,
    };

    private async Task<WebhookEvent?> FreshFetchAsync(Guid id) =>
        await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);

    private static async Task WaitForStatusAsync(
        Func<Task<WebhookEvent?>> fetch, EventStatus status, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            var updated = await fetch();
            if (updated?.Status == status) return;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"Event did not reach status {status} within {timeoutMs} ms");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task EventNotFound_LogsWarningAndContinues()
    {
        // Use a real event as a sentinel: once it reaches Forwarded we know the
        // unknown-ID item before it was also processed (channel is FIFO).
        var sentinel = MakeEvent();
        await _repo.AddAsync(sentinel);

        var (worker, queue, handler) = BuildWorker(HttpStatusCode.OK);
        await worker.StartAsync(CancellationToken.None);

        await queue.EnqueueAsync(Guid.NewGuid()); // unknown — no DB row
        await queue.EnqueueAsync(sentinel.Id);

        await WaitForStatusAsync(() => FreshFetchAsync(sentinel.Id), EventStatus.Forwarded);
        await worker.StopAsync(CancellationToken.None);

        // Only the sentinel caused an HTTP call; the unknown ID was skipped without one.
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task SuccessOnFirstAttempt_SetsForwarded()
    {
        var evt = MakeEvent();
        await _repo.AddAsync(evt);

        var (worker, queue, handler) = BuildWorker(HttpStatusCode.OK);
        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(evt.Id);

        await WaitForStatusAsync(() => FreshFetchAsync(evt.Id), EventStatus.Forwarded);
        await worker.StopAsync(CancellationToken.None);

        var updated = await FreshFetchAsync(evt.Id);
        Assert.NotNull(updated);
        Assert.Equal(EventStatus.Forwarded, updated.Status);
        Assert.Equal(1, updated.ReplayCount);
        Assert.NotNull(updated.ForwardedAt);
        Assert.NotNull(updated.LastReplayAt);
        Assert.Equal(1, handler.CallCount); // one attempt, succeeded immediately
    }

    [Fact]
    public async Task FailTwiceThenSucceed_SetsForwarded()
    {
        var evt = MakeEvent();
        await _repo.AddAsync(evt);

        var (worker, queue, handler) = BuildWorker(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.OK);
        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(evt.Id);

        await WaitForStatusAsync(() => FreshFetchAsync(evt.Id), EventStatus.Forwarded);
        await worker.StopAsync(CancellationToken.None);

        var updated = await FreshFetchAsync(evt.Id);
        Assert.NotNull(updated);
        Assert.Equal(EventStatus.Forwarded, updated.Status);
        Assert.Equal(1, updated.ReplayCount);
        Assert.Equal(3, handler.CallCount); // fail, fail, succeed = 3 HTTP calls
    }

    [Fact]
    public async Task AllAttemptsFail_SetsReplayFailed()
    {
        var evt = MakeEvent();
        await _repo.AddAsync(evt);

        var (worker, queue, handler) = BuildWorker(HttpStatusCode.InternalServerError);
        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(evt.Id);

        await WaitForStatusAsync(() => FreshFetchAsync(evt.Id), EventStatus.ReplayFailed);
        await worker.StopAsync(CancellationToken.None);

        var updated = await FreshFetchAsync(evt.Id);
        Assert.NotNull(updated);
        Assert.Equal(EventStatus.ReplayFailed, updated.Status);
        Assert.Equal(1, updated.ReplayCount);
        Assert.Contains("500", updated.LastError);
        Assert.Equal(4, handler.CallCount); // 1 initial + 3 retries = 4 total HTTP calls
    }

    private sealed class SequencedHandler(params HttpStatusCode[] codes) : HttpMessageHandler
    {
        private int _index;

        public int CallCount => Volatile.Read(ref _index);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var i = Interlocked.Increment(ref _index) - 1;
            var code = i < codes.Length ? codes[i] : codes[^1];
            return Task.FromResult(new HttpResponseMessage(code));
        }
    }
}
