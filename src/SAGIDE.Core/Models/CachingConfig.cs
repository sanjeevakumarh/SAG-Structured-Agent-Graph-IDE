namespace SAGIDE.Core.Models;

/// <summary>
/// Binds the <c>SAGIDE:Caching</c> config section.
/// Centralizes cache TTLs and enable/disable toggles for all in-memory and SQLite caches.
/// </summary>
public class CachingConfig
{
    /// <summary>
    /// When true (default), LLM outputs are cached in SQLite by SHA-256(prompt+model+provider).
    /// Identical prompts skip the LLM call and return the cached response.
    /// Set to false during iterative testing to force fresh LLM calls every time.
    /// </summary>
    public bool OutputCacheEnabled { get; set; } = true;

    /// <summary>
    /// TTL for SearXNG web-search query results in minutes (default 30).
    /// Set to 0 to disable search caching entirely.
    /// </summary>
    public int SearchCacheTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Polling interval for Ollama host health checks in seconds (default 30).
    /// Lower values detect model/host changes faster; higher values reduce network chatter.
    /// </summary>
    public int HealthPollIntervalSeconds { get; set; } = 30;

    /// <summary>
    /// TTL for performance-based routing hint summaries in seconds (default 60).
    /// Stale-while-revalidate: reads always return instantly; refresh happens in background.
    /// </summary>
    public int RoutingHintsTtlSeconds { get; set; } = 60;
}
