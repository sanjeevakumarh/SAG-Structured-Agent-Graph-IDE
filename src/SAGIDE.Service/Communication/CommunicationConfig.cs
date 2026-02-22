namespace SAGIDE.Service.Communication;

/// <summary>
/// IPC / named-pipe configuration bound from SAGIDE:Communication in appsettings.json.
/// </summary>
public class CommunicationConfig
{
    /// <summary>
    /// Maximum allowed size of a single IPC message frame in bytes.
    /// Default 10 MB — raise if large workflow payloads exceed this limit.
    /// </summary>
    public int MaxMessageSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Initial delay (ms) after a pipe-accept error before retrying.</summary>
    public int AcceptRetryInitialDelayMs { get; set; } = 100;

    /// <summary>Maximum delay (ms) for exponential back-off on repeated accept errors.</summary>
    public int AcceptRetryMaxDelayMs { get; set; } = 5_000;

    /// <summary>Back-off multiplier applied to the delay after each consecutive error.</summary>
    public double AcceptRetryBackoffMultiplier { get; set; } = 2.0;

    /// <summary>
    /// Per-client write timeout in seconds during BroadcastAsync / SendToClientAsync.
    /// A stalled client that holds the write lock longer than this is disconnected.
    /// Default 5 s — raise only if clients legitimately need more time to drain the pipe.
    /// </summary>
    public int PerClientBroadcastTimeoutSec { get; set; } = 5;

    /// <summary>
    /// Capacity of the bounded broadcast channel (C4).
    /// When full, the oldest undelivered broadcast is dropped so the producer never blocks.
    /// Default 10 000 — covers ~20 s of 500-token/s streaming at typical message sizes.
    /// </summary>
    public int MaxBroadcastQueueSize { get; set; } = 10_000;
}
