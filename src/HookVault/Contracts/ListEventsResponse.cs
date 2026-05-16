namespace HookVault.Contracts;

public sealed record ListEventsResponse(
    IReadOnlyList<EventSummary> Items,
    int? Total,
    int Limit,
    int Offset,
    bool TotalApproximate = false);
