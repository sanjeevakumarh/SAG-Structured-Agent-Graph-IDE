using System.Diagnostics;

namespace SAGIDE.Observability;

/// <summary>
/// Central registry of <see cref="ActivitySource"/> instances — one per Agent OS module.
///
/// Each module owns its source name so traces show clean module boundaries in any
/// compatible backend (Aspire Dashboard, Jaeger, Tempo, etc.).
///
/// Usage:
/// <code>
///   using var activity = SagideActivitySource.Orchestrator.StartActivity("task.dispatch");
///   activity?.SetTag("task.id", taskId);
/// </code>
///
/// When the service runs distributed (each module in its own container) these sources
/// stay the same — the W3C traceparent header carries the context across the wire
/// automatically via OpenTelemetry's HttpClient instrumentation.
/// </summary>
public static class SagideActivitySource
{
    public const string ServiceName    = "SAGIDE";
    public const string ServiceVersion = "1.0.0";

    // ── Per-module sources ────────────────────────────────────────────────────

    /// <summary>REST API and named-pipe entry points — the root span for every operation.</summary>
    public static readonly ActivitySource Api = new("SAGIDE.Api", ServiceVersion);

    /// <summary>Agent task dispatch, queue operations, and LLM provider calls.</summary>
    public static readonly ActivitySource Orchestrator = new("SAGIDE.Orchestrator", ServiceVersion);

    /// <summary>Workflow engine: DAG evaluation, step dispatch, approval gates.</summary>
    public static readonly ActivitySource Workflow = new("SAGIDE.Workflow", ServiceVersion);

    /// <summary>Model routing: health checks, host selection, failover decisions.</summary>
    public static readonly ActivitySource ModelRouter = new("SAGIDE.ModelRouter", ServiceVersion);

    /// <summary>RAG pipeline: retrieval, chunking, embedding, vector search.</summary>
    public static readonly ActivitySource Memory = new("SAGIDE.Memory", ServiceVersion);

    /// <summary>Tool calls: git, filesystem, web fetch, search.</summary>
    public static readonly ActivitySource Tools = new("SAGIDE.Tools", ServiceVersion);

    /// <summary>Scheduler: cron trigger, job dispatch.</summary>
    public static readonly ActivitySource Scheduler = new("SAGIDE.Scheduler", ServiceVersion);

    // ── All sources (for OpenTelemetry registration) ──────────────────────────

    /// <summary>
    /// All source names — pass to <c>AddSource()</c> when configuring OpenTelemetry tracing
    /// so every module's spans are captured by the SDK.
    /// </summary>
    public static readonly IReadOnlyList<string> AllSourceNames =
    [
        Api.Name,
        Orchestrator.Name,
        Workflow.Name,
        ModelRouter.Name,
        Memory.Name,
        Tools.Name,
        Scheduler.Name,
    ];

    // ── Convenience helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Starts an activity and sets the standard SAGIDE tags on it.
    /// Returns null when the source has no listeners (zero overhead in production
    /// when no exporter is configured).
    /// </summary>
    public static Activity? Start(
        ActivitySource source,
        string operationName,
        ActivityKind kind = ActivityKind.Internal,
        string? traceId   = null)
    {
        var activity = source.StartActivity(operationName, kind);
        if (activity is null) return null;

        if (traceId is not null)
            activity.SetTag("sagide.trace_id", traceId);

        return activity;
    }
}
