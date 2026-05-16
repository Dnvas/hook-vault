namespace HookVault.Services;

// Unit of work for the replay queue. EventId identifies the WebhookEvent;
// BodyOverride, when non-null, replaces the stored Body for this single
// replay attempt (in-UI body-edit flow). The override is not persisted;
// the WebhookEvent retains its original captured body.
public sealed record ReplayJob(Guid EventId, byte[]? BodyOverride = null);
