using HookVault.Domain;
using Microsoft.EntityFrameworkCore;

namespace HookVault.Infrastructure;

public class EventRepository(HookVaultDbContext db)
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

    public Task<DateTimeOffset?> OldestEventAtAsync(CancellationToken ct = default)
        => db.Events.OrderBy(e => e.ReceivedAt).Select(e => (DateTimeOffset?)e.ReceivedAt).FirstOrDefaultAsync(ct);

    public async Task<int> DeleteAsync(string? provider = null, CancellationToken ct = default)
    {
        var query = db.Events.AsQueryable();
        if (!string.IsNullOrEmpty(provider))
            query = query.Where(e => e.Provider == provider);

        var items = await query.ToListAsync(ct);
        db.Events.RemoveRange(items);
        await db.SaveChangesAsync(ct);
        return items.Count;
    }
}
