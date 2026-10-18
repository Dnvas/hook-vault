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
    // Named shared-cache in-memory DB: each DbContext opens its own connection,
    // so the test's polling reads and the worker's writes never share a
    // (non-thread-safe) SqliteConnection. _keepAlive holds the DB open.
    private readonly string _connectionString =
        $"DataSource=file:{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAlive;
    private readonly HookVaultDbContext _db;
    private readonly EventRepository _repo;
    private readonly List<ServiceProvider> _providers = [];

    public ReplayWorkerTests()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        var options = new DbContextOptionsBuilder<HookVaultDbContext>()
            .UseSqlite(_connectionString)
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
        await _keepAlive.DisposeAsync();
    }

    private (ReplayWorker worker, ReplayQueue queue, SequencedHandler handler) BuildWorker(
        params HttpStatusCode[] responses)
    {
        var handler = new SequencedHandler(responses);

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<HookVaultDbContext>(opts => opts.UseSqlite(_connectionString));
        services.AddScoped<EventRepository>();
        services.AddSingleton<HookVault.Observability.HookVaultMeter>();
        services.AddScoped<EventForwarder>();
        services.AddHttpClient("forwarder")
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        var queue = new ReplayQueue();
        var worker = new ReplayWorker(queue, provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<HookVault.Observability.HookVaultMeter>(), NullLogger<ReplayWorker>.Instance)
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
        Body = System.Text.Encoding.UTF8.GetBytes("hello"),
        ForwardUrl = "http://localhost/webhook",
        Status = EventStatus.ForwardFailed,
    };

    private async Task<WebhookEvent?> FreshFetchAsync(Guid id) =>
        await _db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id);

    private static async Task WaitForStatusAsync(
        ReplayWorker worker, Func<Task<WebhookEvent?>> fetch, EventStatus status, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            // A faulted worker stops draining the queue; surface its exception
            // instead of waiting out the timeout.
            if (worker.ExecuteTask is { IsFaulted: true } faulted)
                await faulted;

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

        await WaitForStatusAsync(worker, () => FreshFetchAsync(sentinel.Id), EventStatus.Forwarded);
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

        await WaitForStatusAsync(worker, () => FreshFetchAsync(evt.Id), EventStatus.Forwarded);
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

        await WaitForStatusAsync(worker, () => FreshFetchAsync(evt.Id), EventStatus.Forwarded);
        await worker.StopAsync(CancellationToken.None);

        var updated = await FreshFetchAsync(evt.Id);
        Assert.NotNull(updated);
        Assert.Equal(EventStatus.Forwarded, updated.Status);
        Assert.Equal(1, updated.ReplayCount);
        Assert.Equal(3, handler.CallCount); // fail, fail, succeed = 3 HTTP calls
    }

    [Fact]
    public async Task NonRetriable4xx_StopsAfterFirstAttempt()
    {
        var evt = MakeEvent();
        await _repo.AddAsync(evt);

        var (worker, queue, handler) = BuildWorker(HttpStatusCode.NotFound);
        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(evt.Id);

        await WaitForStatusAsync(worker, () => FreshFetchAsync(evt.Id), EventStatus.ReplayFailed);
        await worker.StopAsync(CancellationToken.None);

        var updated = await FreshFetchAsync(evt.Id);
        Assert.NotNull(updated);
        Assert.Equal(EventStatus.ReplayFailed, updated.Status);
        Assert.Equal(404, updated.ForwardStatusCode);
        Assert.Equal(1, handler.CallCount); // no retries on non-retriable 4xx
    }

    [Fact]
    public async Task Retriable429_TriggersFullRetrySequence()
    {
        var evt = MakeEvent();
        await _repo.AddAsync(evt);

        var (worker, queue, handler) = BuildWorker((HttpStatusCode)429);
        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(evt.Id);

        await WaitForStatusAsync(worker, () => FreshFetchAsync(evt.Id), EventStatus.ReplayFailed);
        await worker.StopAsync(CancellationToken.None);

        var updated = await FreshFetchAsync(evt.Id);
        Assert.NotNull(updated);
        Assert.Equal(EventStatus.ReplayFailed, updated.Status);
        Assert.Equal(4, handler.CallCount); // 429 stays retriable → 1 initial + 3 retries
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(400, false)]
    [InlineData(401, false)]
    [InlineData(403, false)]
    [InlineData(404, false)]
    [InlineData(408, true)]
    [InlineData(422, false)]
    [InlineData(425, true)]
    [InlineData(429, true)]
    [InlineData(500, true)]
    [InlineData(502, true)]
    [InlineData(503, true)]
    [InlineData(null, true)]
    public void IsRetriable_MatchesContract(int? status, bool expected)
    {
        Assert.Equal(expected, ReplayWorker.IsRetriable(status));
    }

    [Fact]
    public async Task RetriableFailureThenSuccess_IncrementsRetryMetric()
    {
        var evt = MakeEvent();
        await _repo.AddAsync(evt);

        var (worker, queue, _) = BuildWorker(
            HttpStatusCode.InternalServerError,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.OK);

        using var listener = new System.Diagnostics.Metrics.MeterListener();
        var retryCount = 0L;
        var successCount = 0L;
        listener.InstrumentPublished = (inst, l) =>
        {
            if (inst.Meter.Name == HookVault.Observability.HookVaultMeter.MeterName
                && inst.Name == "hookvault_replays_total")
                l.EnableMeasurementEvents(inst);
        };
        listener.SetMeasurementEventCallback<long>((inst, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome" && tag.Value is string s)
                {
                    if (s == "retry") Interlocked.Add(ref retryCount, value);
                    else if (s == "success") Interlocked.Add(ref successCount, value);
                }
            }
        });
        listener.Start();

        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(evt.Id);

        await WaitForStatusAsync(worker, () => FreshFetchAsync(evt.Id), EventStatus.Forwarded);
        await worker.StopAsync(CancellationToken.None);
        listener.Dispose();

        Assert.Equal(2, Interlocked.Read(ref retryCount));
        Assert.Equal(1, Interlocked.Read(ref successCount));
    }

    [Fact]
    public async Task AllAttemptsFail_SetsReplayFailed()
    {
        var evt = MakeEvent();
        await _repo.AddAsync(evt);

        var (worker, queue, handler) = BuildWorker(HttpStatusCode.InternalServerError);
        await worker.StartAsync(CancellationToken.None);
        await queue.EnqueueAsync(evt.Id);

        await WaitForStatusAsync(worker, () => FreshFetchAsync(evt.Id), EventStatus.ReplayFailed);
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
