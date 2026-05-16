using System.Diagnostics.Metrics;

namespace HookVault.Observability;

// Singleton hosting all custom metric instruments. Registered in DI; injected
// into IngestController, ReplayWorker, EventForwarder, and EventRetentionWorker.
// The Meter name "HookVault" is the same string passed to `AddMeter` in the
// OpenTelemetry registration in Program.cs.
public sealed class HookVaultMeter : IDisposable
{
    public const string MeterName = "HookVault";

    private readonly Meter _meter;

    public Counter<long> EventsTotal { get; }
    public Counter<long> ReplaysTotal { get; }
    public Histogram<double> ForwardDurationSeconds { get; }
    public Counter<long> RetentionDeletedTotal { get; }
    public Counter<long> SignatureValidationTotal { get; }

    public HookVaultMeter()
    {
        _meter = new Meter(MeterName, "1.0.0");

        EventsTotal = _meter.CreateCounter<long>(
            "hookvault_events_total",
            unit: "{events}",
            description: "Total webhook events captured, labelled by provider and status.");

        ReplaysTotal = _meter.CreateCounter<long>(
            "hookvault_replays_total",
            unit: "{replays}",
            description: "Total replay attempts, labelled by outcome (success / failed / exhausted).");

        ForwardDurationSeconds = _meter.CreateHistogram<double>(
            "hookvault_forward_duration_seconds",
            unit: "s",
            description: "Latency of forward HTTP calls to the configured forwardUrl.");

        RetentionDeletedTotal = _meter.CreateCounter<long>(
            "hookvault_retention_deleted_total",
            unit: "{events}",
            description: "Total events deleted by the retention worker, labelled by reason.");

        SignatureValidationTotal = _meter.CreateCounter<long>(
            "hookvault_signature_validation_total",
            unit: "{validations}",
            description: "Signature checks performed, labelled by provider and result.");
    }

    public void Dispose() => _meter.Dispose();
}
