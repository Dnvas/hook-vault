namespace HookVault.Domain;

public enum EventStatus
{
    Received = 0,
    Forwarding = 1,
    Forwarded = 2,
    ForwardFailed = 3,
    Replaying = 4,
    ReplayFailed = 5,
}
