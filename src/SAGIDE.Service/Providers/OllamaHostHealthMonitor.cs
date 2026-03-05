using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Service.Routing;

namespace SAGIDE.Service.Providers;

/// <summary>
/// Resource-Aware Multi-Host Scheduler.
/// Background service that periodically polls every configured Ollama server's /api/ps
/// endpoint to determine which models are currently loaded in VRAM.
///
/// Exposes TryGetBestHost() which routes model requests to:
///   1. A reachable server that already has the model hot in VRAM  (avoids swap time)
///   2. Any other reachable server from the known set               (failover)
///   3. null — caller falls back to static routing table
/// </summary>
public sealed class OllamaHostHealthMonitor : BackgroundService
{
    private readonly List<string> _allUrls; // all known Ollama base URLs
    private readonly ILogger<OllamaHostHealthMonitor> _logger;
    private readonly ConcurrentDictionary<string, HostState> _state = new();
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private readonly EndpointAliasResolver? _aliasResolver;

    private sealed record HostState(
        bool IsReachable,
        IReadOnlyList<string> LoadedModels,
        IReadOnlyList<string> InstalledModels,
        DateTime LastSeen);

    public OllamaHostHealthMonitor(
        IEnumerable<string> knownUrls,
        ILogger<OllamaHostHealthMonitor> logger,
        EndpointAliasResolver? aliasResolver = null)
    {
        _logger        = logger;
        _aliasResolver = aliasResolver;
        _allUrls       = knownUrls.Select(u => u.TrimEnd('/')).Distinct().ToList();

        // Initialize all hosts as unknown until the first poll
        foreach (var url in _allUrls)
            _state[url] = new HostState(false, [], [], DateTime.MinValue);
    }

    // ── BackgroundService ─────────────────────────────────────────────────────

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (_allUrls.Count == 0) return;

        var displayUrls = _aliasResolver is not null
            ? string.Join(", ", _allUrls.Select(u => _aliasResolver.GetAlias(u)))
            : string.Join(", ", _allUrls);
        _logger.LogInformation(
            "OllamaHostHealthMonitor started — tracking {Count} server(s): {Servers}",
            _allUrls.Count, displayUrls);

        // Poll immediately so routing is available before the first workflow step
        await PollAllAsync(ct);

        using var timer = new PeriodicTimer(PollInterval);
        while (await timer.WaitForNextTickAsync(ct))
            await PollAllAsync(ct);
    }

    // ── Public query API ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns the best Ollama base URL to use for the given model:
    ///   1. <paramref name="preferredUrl"/> if it is reachable (respects static routing)
    ///   2. A reachable URL from <paramref name="candidates"/> that has the model already loaded
    ///   3. Any other reachable URL from <paramref name="candidates"/>
    ///   4. null — all candidates are unreachable
    /// </summary>
    public string? TryGetBestHost(string modelId, string preferredUrl, IEnumerable<string> candidates)
    {
        var list = candidates.Select(u => u.TrimEnd('/')).Distinct().ToList();

        // rule 1: honor static routing if the preferred host is reachable
        var normalized = preferredUrl.TrimEnd('/');
        if (_state.TryGetValue(normalized, out var preferred) && preferred.IsReachable)
            return normalized;

        // rule 2: pick a reachable host that has the model warm in VRAM
        var warmHost = list.FirstOrDefault(url =>
            _state.TryGetValue(url, out var s) && s.IsReachable &&
            s.LoadedModels.Contains(modelId, StringComparer.OrdinalIgnoreCase));
        if (warmHost is not null)
        {
            _logger.LogInformation(
                "OllamaHealthMonitor: routing {Model} to warm host {Server} (preferred {Pref} unreachable)",
                modelId,
                _aliasResolver?.GetAlias(warmHost) ?? warmHost,
                _aliasResolver?.GetAlias(preferredUrl) ?? preferredUrl);
            return warmHost;
        }

        // rule 3: pick a reachable host that has the model installed (can load it)
        var installedHost = list.FirstOrDefault(url =>
            _state.TryGetValue(url, out var s) && s.IsReachable &&
            s.InstalledModels.Contains(modelId, StringComparer.OrdinalIgnoreCase));
        if (installedHost is not null)
        {
            _logger.LogInformation(
                "OllamaHealthMonitor: routing {Model} to host {Server} (model installed, preferred {Pref} unreachable)",
                modelId,
                _aliasResolver?.GetAlias(installedHost) ?? installedHost,
                _aliasResolver?.GetAlias(preferredUrl) ?? preferredUrl);
            return installedHost;
        }

        // rule 4: no reachable host has the model — return null so caller backs off
        _logger.LogWarning(
            "OllamaHealthMonitor: no reachable host has model {Model} installed (preferred {Pref} unreachable)",
            modelId, _aliasResolver?.GetAlias(preferredUrl) ?? preferredUrl);
        return null;
    }

    /// <summary>Returns all Ollama base URLs that were reachable on the last poll.</summary>
    public IReadOnlyList<string> GetAllReachableHosts()
        => _allUrls.Where(u => _state.TryGetValue(u, out var s) && s.IsReachable).ToList();

    /// <summary>Returns reachable hosts that have the specified model installed.</summary>
    public IReadOnlyList<string> GetReachableHostsWithModel(string modelId)
        => _allUrls.Where(u => _state.TryGetValue(u, out var s) && s.IsReachable &&
            s.InstalledModels.Contains(modelId, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Returns true if the given server was reachable on the last poll.</summary>
    public bool IsReachable(string baseUrl)
        => _state.TryGetValue(baseUrl.TrimEnd('/'), out var s) && s.IsReachable;

    /// <summary>Returns the models currently loaded in VRAM on the given server.</summary>
    public IReadOnlyList<string> GetLoadedModels(string baseUrl)
        => _state.TryGetValue(baseUrl.TrimEnd('/'), out var s) ? s.LoadedModels : [];

    /// <summary>Returns all models installed (available via /api/tags) on the given server.</summary>
    public IReadOnlyList<string> GetInstalledModels(string baseUrl)
        => _state.TryGetValue(baseUrl.TrimEnd('/'), out var s) ? s.InstalledModels : [];

    /// <summary>
    /// Test seam: directly sets the observed state for a host without performing an HTTP poll.
    /// Only used by unit tests via <c>[assembly: InternalsVisibleTo("SAGIDE.Service.Tests")]</c>.
    /// </summary>
    internal void SimulateHostState(string baseUrl, bool isReachable,
        IReadOnlyList<string> loadedModels, IReadOnlyList<string>? installedModels = null)
        => _state[baseUrl.TrimEnd('/')] = new HostState(
            isReachable, loadedModels, installedModels ?? loadedModels, DateTime.UtcNow);

    // ── Private polling ───────────────────────────────────────────────────────

    private Task PollAllAsync(CancellationToken ct)
        => Task.WhenAll(_allUrls.Select(url => PollOneAsync(url, ct)));

    private async Task PollOneAsync(string baseUrl, CancellationToken ct)
    {
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // Poll /api/ps for VRAM-loaded models
            var response = await client.GetAsync($"{baseUrl}/api/ps", ct);
            if (!response.IsSuccessStatusCode)
            {
                _state[baseUrl] = new HostState(false, [], [], DateTime.UtcNow);
                return;
            }

            var loadedModels = ParseModelNames(await response.Content.ReadAsStringAsync(ct));

            // Poll /api/tags for all installed models (available to load)
            var installedModels = new List<string>();
            try
            {
                var tagsResp = await client.GetAsync($"{baseUrl}/api/tags", ct);
                if (tagsResp.IsSuccessStatusCode)
                    installedModels = ParseModelNames(await tagsResp.Content.ReadAsStringAsync(ct));
            }
            catch
            {
                // /api/tags failure is non-fatal — we still have /api/ps data
            }

            var wasReachable = _state.TryGetValue(baseUrl, out var prev) && prev.IsReachable;
            _state[baseUrl] = new HostState(true, loadedModels, installedModels, DateTime.UtcNow);

            var serverLabel = _aliasResolver?.GetAlias(baseUrl) ?? baseUrl;
            if (!wasReachable)
                _logger.LogInformation(
                    "Ollama {Server} is now reachable ({Loaded} loaded, {Installed} installed)",
                    serverLabel, loadedModels.Count, installedModels.Count);
            else
                _logger.LogDebug(
                    "Ollama {Server}: {Loaded} in VRAM, {Installed} installed",
                    serverLabel, loadedModels.Count, installedModels.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var wasReachable = _state.TryGetValue(baseUrl, out var prev) && prev.IsReachable;
            _state[baseUrl] = new HostState(false, [], [], DateTime.UtcNow);
            var serverLabel = _aliasResolver?.GetAlias(baseUrl) ?? baseUrl;
            if (wasReachable)
                _logger.LogWarning("Ollama {Server} became unreachable: {Msg}", serverLabel, ex.Message);
            else
                _logger.LogDebug("Ollama {Server}: unreachable ({Msg})", serverLabel, ex.Message);
        }
    }

    private static List<string> ParseModelNames(string json)
    {
        var models = new List<string>();
        var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("models", out var modelsEl))
        {
            foreach (var m in modelsEl.EnumerateArray())
            {
                // /api/ps uses "name", /api/tags uses "name" (both have it)
                if (m.TryGetProperty("name", out var nameEl) &&
                    nameEl.GetString() is { Length: > 0 } name)
                    models.Add(name);
            }
        }
        return models;
    }
}
