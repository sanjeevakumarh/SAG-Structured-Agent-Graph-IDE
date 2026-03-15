using System.Diagnostics;

namespace SAGIDE.Observability;

/// <summary>
/// Ambient correlation context that flows through <see cref="AsyncLocal{T}"/> — no
/// parameter threading required.
///
/// A <see cref="TraceId"/> is stamped once at each entry point (REST request, named
/// pipe message, scheduled job) and is then readable anywhere in the call chain,
/// including across <c>await</c> boundaries and <c>Task.Run</c> continuations.
///
/// The ID is also set as a tag on the current <see cref="Activity"/> so it appears
/// in every span emitted during the operation, enabling full correlation across
/// module boundaries in any OpenTelemetry-compatible backend.
///
/// Relationship to <see cref="Activity"/>:
/// - <see cref="Activity.TraceId"/> is the W3C trace ID used for distributed tracing.
/// - <see cref="TraceContext.TraceId"/> reuses that same ID when an Activity is active,
///   and falls back to a short random ID when no Activity is present (e.g. background jobs).
///   This means the two IDs are always consistent — you can correlate log lines with traces.
/// </summary>
public static class TraceContext
{
    private static readonly AsyncLocal<string?> _traceId       = new();
    private static readonly AsyncLocal<string?> _operationName = new();
    private static readonly AsyncLocal<string?> _sourceTag     = new();

    // ── Read-only accessors ───────────────────────────────────────────────────

    /// <summary>
    /// Current trace ID. Consistent with <see cref="Activity.Current"/> when an
    /// activity is active; otherwise a short random hex string set via <see cref="Start"/>.
    /// Never null after <see cref="Start"/> has been called on the execution context.
    /// </summary>
    public static string TraceId => _traceId.Value ?? Activity.Current?.TraceId.ToString() ?? "(none)";

    /// <summary>Human-readable name of the top-level operation (e.g. "POST /api/tasks", "pipe:submit_task").</summary>
    public static string? OperationName => _operationName.Value;

    /// <summary>Frontend source tag that originated the operation (e.g. "vscode", "cli", "scheduler").</summary>
    public static string? SourceTag => _sourceTag.Value;

    // ── Entry-point stamp ─────────────────────────────────────────────────────

    /// <summary>
    /// Stamps the current async context with a correlation ID and operation name.
    /// Should be called once at every system entry point before any await.
    ///
    /// If an <see cref="Activity"/> is already active (e.g. from ASP.NET Core's
    /// HTTP instrumentation), its W3C TraceId is reused so logs and traces share
    /// the same identifier. Otherwise a new short ID is generated.
    ///
    /// Returns an <see cref="IDisposable"/> that clears the context on disposal —
    /// suitable for use in <c>using</c> statements in request handlers or hosted
    /// service loops.
    /// </summary>
    public static IDisposable Start(string operationName, string? sourceTag = null)
    {
        // Prefer the W3C trace ID from an active Activity (set by OTel HTTP instrumentation).
        // Fall back to a compact random ID for non-HTTP entry points.
        var id = Activity.Current?.TraceId.ToString()
                 ?? Guid.NewGuid().ToString("N")[..16];

        _traceId.Value       = id;
        _operationName.Value = operationName;
        _sourceTag.Value     = sourceTag;

        // Tag the current activity so the correlation ID appears in every span.
        Activity.Current?.SetTag("sagide.trace_id",      id);
        Activity.Current?.SetTag("sagide.operation",     operationName);
        if (sourceTag is not null)
            Activity.Current?.SetTag("sagide.source_tag", sourceTag);

        return new ContextScope(id, operationName, sourceTag);
    }

    /// <summary>
    /// Stamps the context with an explicit trace ID (used when resuming a trace
    /// from a W3C traceparent header received over a non-HTTP transport, e.g. named pipes).
    /// </summary>
    public static IDisposable StartWithId(string traceId, string operationName, string? sourceTag = null)
    {
        _traceId.Value       = traceId;
        _operationName.Value = operationName;
        _sourceTag.Value     = sourceTag;

        Activity.Current?.SetTag("sagide.trace_id",      traceId);
        Activity.Current?.SetTag("sagide.operation",     operationName);
        if (sourceTag is not null)
            Activity.Current?.SetTag("sagide.source_tag", sourceTag);

        return new ContextScope(traceId, operationName, sourceTag);
    }

    // ── Scope helper ─────────────────────────────────────────────────────────

    private sealed class ContextScope : IDisposable
    {
        private readonly string?  _prevTraceId;
        private readonly string?  _prevOperation;
        private readonly string?  _prevSourceTag;

        public ContextScope(string traceId, string operationName, string? sourceTag)
        {
            // Snapshot previous values so nested scopes restore correctly.
            _prevTraceId   = _traceId.Value;
            _prevOperation = _operationName.Value;
            _prevSourceTag = _sourceTag.Value;

            _traceId.Value       = traceId;
            _operationName.Value = operationName;
            _sourceTag.Value     = sourceTag;
        }

        public void Dispose()
        {
            _traceId.Value       = _prevTraceId;
            _operationName.Value = _prevOperation;
            _sourceTag.Value     = _prevSourceTag;
        }
    }
}
