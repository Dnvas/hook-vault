using HookVault.Contracts;
using HookVault.Domain;
using Microsoft.EntityFrameworkCore;

namespace HookVault.Infrastructure;

public sealed class EventRepository(HookVaultDbContext db)
{
    public async Task AddAsync(WebhookEvent evt, CancellationToken ct = default)
    {
        db.Events.Add(evt);
        await db.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(WebhookEvent evt, CancellationToken ct = default)
    {
        db.Events.Update(evt);
        await db.SaveChangesAsync(ct);
    }

    public Task<WebhookEvent?> GetByIdAsync(Guid id, CancellationToken ct = default)
        => db.Events.FirstOrDefaultAsync(e => e.Id == id, ct);

    public async Task<(List<WebhookEvent> Items, int Total)> ListAsync(
        string? provider, string? status, DateTimeOffset? from, DateTimeOffset? to,
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var query = db.Events.AsQueryable();

        if (!string.IsNullOrEmpty(provider))
            query = query.Where(e => e.Provider == provider);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<EventStatus>(status, true, out var s))
            query = query.Where(e => e.Status == s);

        if (from.HasValue)
            query = query.Where(e => e.ReceivedAt >= from.Value);

        if (to.HasValue)
            query = query.Where(e => e.ReceivedAt <= to.Value);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(e => e.ReceivedAt)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        return (items, total);
    }

    public async Task<(List<EventSummary> Items, int Total)> ListSummariesAsync(
        string? provider, string? status, DateTimeOffset? from, DateTimeOffset? to,
        string? bodyContains = null, string? providerEventId = null,
        int limit = 50, int offset = 0, CancellationToken ct = default)
    {
        var query = db.Events.AsQueryable();

        if (!string.IsNullOrEmpty(provider))
            query = query.Where(e => e.Provider == provider);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<EventStatus>(status, true, out var s))
            query = query.Where(e => e.Status == s);

        if (from.HasValue)
            query = query.Where(e => e.ReceivedAt >= from.Value);

        if (to.HasValue)
            query = query.Where(e => e.ReceivedAt <= to.Value);

        if (!string.IsNullOrEmpty(providerEventId))
            query = query.Where(e => e.ProviderEventId == providerEventId);

        // total reflects all SQL-filterable predicates; when bodyContains is also
        // active the count is approximate (pre-body-filter) — acceptable for a dev tool.
        var total = await query.CountAsync(ct);

        // Body is a BLOB column; EF Core's SQLite provider cannot translate
        // Encoding.UTF8.GetString(e.Body).Contains(...) to SQL. Over-fetch then
        // post-filter in memory so body search still respects the other SQL filters.
        var rawList = await query
            .OrderByDescending(e => e.ReceivedAt)
            .Skip(offset)
            .Take(bodyContains is null ? limit : limit * 4)
            .ToListAsync(ct);

        if (!string.IsNullOrEmpty(bodyContains))
        {
            rawList = rawList
                .Where(e => System.Text.Encoding.UTF8.GetString(e.Body)
                    .Contains(bodyContains, StringComparison.OrdinalIgnoreCase))
                .Take(limit)
                .ToList();
        }

        var items = rawList
            .Select(e => new EventSummary(
                e.Id,
                e.Provider,
                e.Status.ToString(),
                e.ReceivedAt,
                e.SignatureValid,
                e.ForwardStatusCode,
                e.ReplayCount,
                e.ForwardedAt))
            .ToList();

        return (items, total);
    }

    public Task<List<WebhookEvent>> GetFailedAsync(string? provider = null, CancellationToken ct = default)
    {
        var query = db.Events
            .Where(e => e.Status == EventStatus.ForwardFailed || e.Status == EventStatus.ReplayFailed);

        if (!string.IsNullOrEmpty(provider))
            query = query.Where(e => e.Provider == provider);

        return query.ToListAsync(ct);
    }

    public Task<int> CountAsync(CancellationToken ct = default)
        => db.Events.CountAsync(ct);

    public async Task<DateTimeOffset?> OldestEventAtAsync(CancellationToken ct = default)
    {
        if (!await db.Events.AnyAsync(ct)) return null;
        var min = await db.Events.MinAsync(e => e.ReceivedAt, ct);
        return min;
    }

    public Task<WebhookEvent?> FindDuplicateAsync(
        string provider,
        string bodyHash,
        string? providerEventId,
        DateTimeOffset since,
        CancellationToken ct = default)
    {
        var q = db.Events
            .Where(e => e.Provider == provider && e.BodyHash == bodyHash && e.ReceivedAt >= since);

        if (!string.IsNullOrEmpty(providerEventId))
            q = q.Where(e => e.ProviderEventId == providerEventId);

        return q.OrderByDescending(e => e.ReceivedAt).FirstOrDefaultAsync(ct);
    }

    public async Task<int> DeleteAsync(string? provider = null, CancellationToken ct = default)
    {
        var query = db.Events.AsQueryable();
        if (!string.IsNullOrEmpty(provider))
            query = query.Where(e => e.Provider == provider);

        return await query.ExecuteDeleteAsync(ct);
    }
}
