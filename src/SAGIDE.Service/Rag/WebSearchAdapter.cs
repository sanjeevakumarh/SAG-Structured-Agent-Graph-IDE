using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAGIDE.Service.Persistence;

namespace SAGIDE.Service.Rag;

/// <summary>
/// Sends search queries to a SearXNG instance and returns formatted result text.
/// <para>
/// Search URLs are collected from all <c>Ollama:Servers</c> entries that have a
/// numeric <c>RagOrder</c> field and a non-empty <c>SearchUrl</c>, sorted by
/// <c>RagOrder</c> ascending (0 = primary, 1 = first fallback, …).
/// Each query tries them in sequence and returns the first successful result.
/// The legacy <c>SAGIDE:Rag:SearchUrl</c> flat key is appended as a final fallback.
/// </para>
/// <para>
/// Results are persisted to SQLite via <see cref="SearchCacheRepository"/> with per-domain
/// TTLs. Fresh results are scored by <see cref="SearchQualityScorer"/>; low-quality results
/// (captcha, bot walls) are rejected in favor of stale cached data when available.
/// </para>
/// </summary>
public sealed class WebSearchAdapter
{
    private readonly HttpClient _http;
    private readonly IReadOnlyList<string> _searchUrls;
    private readonly ILogger<WebSearchAdapter> _logger;
    private readonly string? _engines;
    private readonly SearchCacheRepository? _persistentCache;
    private readonly IReadOnlyDictionary<string, int> _domainTtlHours;
    private readonly int _defaultTtlHours;

    // In-memory query cache: query → (result, fetchedAt) — fast L1 cache over persistent L2
    private readonly Dictionary<string, (string result, DateTime fetchedAt)> _cache = [];
    private readonly TimeSpan _cacheTtl;

    public WebSearchAdapter(HttpClient http, IConfiguration configuration, ILogger<WebSearchAdapter> logger,
        SearchCacheRepository? persistentCache = null)
    {
        _http            = http;
        _searchUrls      = ResolveSearchUrls(configuration);
        _logger          = logger;
        _engines         = configuration["SAGIDE:Rag:SearchEngines"];
        _persistentCache = persistentCache;
        var minutes      = configuration.GetValue("SAGIDE:Caching:SearchCacheTtlMinutes", 30);
        _cacheTtl        = minutes > 0 ? TimeSpan.FromMinutes(minutes) : TimeSpan.Zero;

        // Per-domain TTL (hours): SAGIDE:Caching:SearchCacheTtlByDomain:finance = 24, etc.
        _defaultTtlHours = configuration.GetValue("SAGIDE:Caching:PersistentSearchCacheTtlHours", 4);
        var domainTtls = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in configuration.GetSection("SAGIDE:Caching:SearchCacheTtlByDomain").GetChildren())
        {
            if (int.TryParse(child.Value, out var hours))
                domainTtls[child.Key] = hours;
        }
        _domainTtlHours = domainTtls;
    }

    /// <summary>
    /// Collects SearXNG URLs from servers that have a numeric <c>RagOrder</c> and a
    /// non-empty <c>SearchUrl</c>, sorted ascending by <c>RagOrder</c>.
    /// The legacy <c>SAGIDE:Rag:SearchUrl</c> flat key is appended last.
    /// </summary>
    private static IReadOnlyList<string> ResolveSearchUrls(IConfiguration cfg)
    {
        var ordered = new List<(int order, string url)>();

        foreach (var server in cfg.GetSection("SAGIDE:Ollama:Servers").GetChildren())
        {
            if (!int.TryParse(server["RagOrder"], out var order))
                continue; // no RagOrder → inference-only server

            var url = server["SearchUrl"]?.TrimEnd('/');
            // Strip /search path suffix in case the URL was misconfigured with it included
            if (url?.EndsWith("/search", StringComparison.OrdinalIgnoreCase) == true)
                url = url[..^7];
            if (!string.IsNullOrWhiteSpace(url))
                ordered.Add((order, url!));
        }

        var urls = ordered
            .OrderBy(x => x.order)
            .Select(x => x.url)
            .ToList();

        // Legacy flat key — appended last as final fallback
        var legacy = cfg["SAGIDE:Rag:SearchUrl"]?.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(legacy) && !urls.Contains(legacy))
            urls.Add(legacy);

        return urls;
    }

    /// <summary>Returns true if at least one SearXNG URL is configured.</summary>
    public bool IsConfigured => _searchUrls.Count > 0;

    /// <summary>
    /// Searches for <paramref name="query"/> and returns a formatted string of top results.
    /// <para>
    /// Cache strategy (L1 in-memory → L2 persistent SQLite → internet):
    /// <list type="number">
    ///   <item>L1 in-memory cache hit within TTL → return immediately</item>
    ///   <item>L2 persistent cache hit within domain TTL → return + populate L1</item>
    ///   <item>Fetch from SearXNG → score quality → accept or reject</item>
    ///   <item>If rejected and L2 has stale data with good quality → return stale</item>
    ///   <item>If rejected and no L2 → return fresh anyway with warning</item>
    /// </list>
    /// </para>
    /// </summary>
    public async Task<string> SearchAsync(
        string query,
        int maxResults = 5,
        string? domain = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return string.Empty;

        if (!IsConfigured)
        {
            _logger.LogWarning("web_search_batch: no SearXNG URL configured — skipping query '{Query}'", query);
            return string.Empty;
        }

        var cacheKey = $"{query}|{maxResults}";
        var domainKey = domain ?? "default";
        var ttlHours = _domainTtlHours.TryGetValue(domainKey, out var h) ? h : _defaultTtlHours;

        // L1: in-memory cache hit
        if (_cacheTtl > TimeSpan.Zero
            && _cache.TryGetValue(cacheKey, out var cached)
            && DateTime.UtcNow - cached.fetchedAt < _cacheTtl)
        {
            return cached.result;
        }

        // L2: persistent cache hit (within domain TTL)
        var queryHash = SearchCacheRepository.HashQuery(query, maxResults);
        SearchCacheEntry? persistedEntry = null;
        if (_persistentCache is not null)
        {
            persistedEntry = await _persistentCache.GetAsync(queryHash);
            if (persistedEntry is not null)
            {
                var age = DateTime.UtcNow - DateTime.Parse(persistedEntry.FetchedAt);
                if (age < TimeSpan.FromHours(ttlHours))
                {
                    _logger.LogDebug("Persistent cache hit for '{Query}' (age={Age:F1}h, domain={Domain})",
                        query, age.TotalHours, domainKey);
                    _cache[cacheKey] = (persistedEntry.ResultText, DateTime.UtcNow);
                    return persistedEntry.ResultText;
                }
            }
        }

        // L3: fetch from internet
        var (freshResult, freshCount) = await FetchFromSearchEnginesAsync(query, maxResults, ct);

        if (string.IsNullOrEmpty(freshResult))
        {
            // Total failure — use stale cache if available
            if (persistedEntry is not null && persistedEntry.QualityScore >= SearchQualityScorer.AcceptThreshold)
            {
                _logger.LogWarning(
                    "All search engines failed for '{Query}' — using stale cache (age={Age})",
                    query, DateTime.UtcNow - DateTime.Parse(persistedEntry.FetchedAt));
                var staleResult = persistedEntry.ResultText + $"\n\n[Stale data from {persistedEntry.FetchedAt} — live search failed]";
                _cache[cacheKey] = (staleResult, DateTime.UtcNow);
                return staleResult;
            }
            return string.Empty;
        }

        // Score quality
        var (score, reason) = SearchQualityScorer.Score(freshResult, freshCount);

        if (score >= SearchQualityScorer.AcceptThreshold)
        {
            // Good fresh data — persist and return
            _cache[cacheKey] = (freshResult, DateTime.UtcNow);
            if (_persistentCache is not null)
            {
                await _persistentCache.UpsertAsync(new SearchCacheEntry(
                    queryHash, query, freshResult, freshCount, score, domainKey, DateTime.UtcNow.ToString("O")));
            }
            if (score < 0.5)
                _logger.LogDebug("Search result for '{Query}' has marginal quality (score={Score}, reason={Reason})",
                    query, score, reason);
            return freshResult;
        }

        // Bad fresh data — prefer stale cache
        _logger.LogWarning(
            "Fresh search rejected for '{Query}' (score={Score}, reason={Reason})",
            query, score, reason);

        if (persistedEntry is not null && persistedEntry.QualityScore >= SearchQualityScorer.AcceptThreshold)
        {
            _logger.LogInformation(
                "Using stale cache for '{Query}' (cached score={CachedScore}, age={Age})",
                query, persistedEntry.QualityScore,
                DateTime.UtcNow - DateTime.Parse(persistedEntry.FetchedAt));
            var staleResult = persistedEntry.ResultText +
                $"\n\n[Stale data from {persistedEntry.FetchedAt} — fresh search returned low-quality results ({reason})]";
            _cache[cacheKey] = (staleResult, DateTime.UtcNow);
            return staleResult;
        }

        // No good cache — return fresh with warning (better than nothing)
        _logger.LogWarning("No cached alternative for '{Query}' — returning low-quality results", query);
        if (_persistentCache is not null)
        {
            await _persistentCache.UpsertAsync(new SearchCacheEntry(
                queryHash, query, freshResult, freshCount, score, domainKey, DateTime.UtcNow.ToString("O")));
        }
        _cache[cacheKey] = (freshResult, DateTime.UtcNow);
        return freshResult + $"\n\n[Warning: search results may be low quality ({reason})]";
    }

    /// <summary>Backwards-compatible overload without domain parameter.</summary>
    public Task<string> SearchAsync(string query, int maxResults, CancellationToken ct) =>
        SearchAsync(query, maxResults, domain: null, ct);

    // ── Internet fetch ───────────────────────────────────────────────────────

    private async Task<(string Result, int Count)> FetchFromSearchEnginesAsync(
        string query, int maxResults, CancellationToken ct)
    {
        var encodedQuery = Uri.EscapeDataString(query);

        foreach (var baseUrl in _searchUrls)
        {
            try
            {
                var url = string.IsNullOrWhiteSpace(_engines)
                    ? $"{baseUrl}/search?q={encodedQuery}&format=json&categories=general"
                    : $"{baseUrl}/search?q={encodedQuery}&format=json&engines={Uri.EscapeDataString(_engines)}";
                using var response = await _http.GetAsync(url, ct);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("SearXNG at {BaseUrl} returned {Status} for query '{Query}' — trying next",
                        baseUrl, (int)response.StatusCode, query);
                    continue;
                }

                var json = await response.Content.ReadAsStringAsync(ct);
                return ParseSearxngResponse(json, maxResults);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "SearXNG at {BaseUrl} failed for query '{Query}' — trying next", baseUrl, query);
            }
        }

        return (string.Empty, 0);
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    private static (string Text, int Count) ParseSearxngResponse(string json, int maxResults)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var resultsEl))
                return (string.Empty, 0);

            var sb = new System.Text.StringBuilder();
            var count = 0;

            foreach (var result in resultsEl.EnumerateArray())
            {
                if (count >= maxResults) break;

                var title   = GetStr(result, "title")   ?? "(no title)";
                var url     = GetStr(result, "url")     ?? string.Empty;
                var snippet = GetStr(result, "content") ?? string.Empty;

                sb.AppendLine($"[{count + 1}] {title}");
                if (!string.IsNullOrEmpty(url))     sb.AppendLine($"    URL: {url}");
                if (!string.IsNullOrEmpty(snippet)) sb.AppendLine($"    {snippet}");
                sb.AppendLine();

                count++;
            }

            return (sb.ToString().TrimEnd(), count);
        }
        catch
        {
            return (json, 0); // return raw on parse failure
        }
    }

    private static string? GetStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
