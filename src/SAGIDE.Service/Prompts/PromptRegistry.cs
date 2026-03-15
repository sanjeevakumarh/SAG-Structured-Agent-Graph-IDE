using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SAGIDE.Service.Prompts;

/// <summary>
/// Loads all prompt YAML files from the configured PromptsPath directory, indexes them by
/// (domain, name), and hot-reloads when files change on disk.
/// Also supports API-based registration for external applications.
/// </summary>
public sealed class PromptRegistry : IPromptRegistry, IPromptRegistrationService, IDisposable
{
    private readonly string _promptsRoot;
    private readonly ILogger<PromptRegistry> _logger;
    private readonly IDeserializer _yaml;
    private readonly FileSystemWatcher _watcher;

    // File-loaded prompts — rebuilt on every file change
    private volatile Dictionary<string, PromptDefinition> _fileIndex = [];

    // API-registered prompts — survive file reloads
    private readonly Dictionary<string, PromptDefinition> _apiIndex = new(StringComparer.Ordinal);
    private readonly object _apiLock = new();

    public PromptRegistry(IConfiguration configuration, IHostEnvironment env, ILogger<PromptRegistry> logger)
    {
        _logger = logger;

        var configuredPath = configuration["SAGIDE:PromptsPath"] ?? "prompts";
        _promptsRoot = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configuredPath));

        _yaml = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        LoadAll();

        if (Directory.Exists(_promptsRoot))
        {
            _watcher = new FileSystemWatcher(_promptsRoot, "*.yaml")
            {
                IncludeSubdirectories = true,
                EnableRaisingEvents   = true,
            };
            _watcher.Changed += (_, _) => LoadAll();
            _watcher.Created += (_, _) => LoadAll();
            _watcher.Deleted += (_, _) => LoadAll();
            _watcher.Renamed += (_, _) => LoadAll();
        }
        else
        {
            _watcher = new FileSystemWatcher { EnableRaisingEvents = false };
        }
    }

    // ── IPromptRegistry (read) ────────────────────────────────────────────────

    public IReadOnlyList<PromptDefinition> GetAll() => [.. MergedIndex().Values];

    public IReadOnlyList<PromptDefinition> GetByDomain(string domain)
    {
        var prefix = domain.ToLowerInvariant() + "/";
        return [.. MergedIndex()
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => kv.Value)];
    }

    public PromptDefinition? GetByKey(string domain, string name)
    {
        var key = MakeKey(domain, name);
        return MergedIndex().TryGetValue(key, out var def) ? def : null;
    }

    public IReadOnlyList<PromptDefinition> GetScheduled() =>
        [.. MergedIndex().Values.Where(p => !string.IsNullOrWhiteSpace(p.Schedule))];

    // ── IPromptRegistrationService (write) ────────────────────────────────────

    public void Register(PromptDefinition prompt)
    {
        if (string.IsNullOrEmpty(prompt.Name) || string.IsNullOrEmpty(prompt.Domain))
            throw new ArgumentException("Prompt must have both name and domain set.");

        prompt.Source = "api";
        var key = MakeKey(prompt.Domain, prompt.Name);
        lock (_apiLock)
        {
            _apiIndex[key] = prompt;
        }
        _logger.LogInformation("API-registered prompt: {Domain}/{Name} v{Version}", prompt.Domain, prompt.Name, prompt.Version);
    }

    public void RegisterBulk(IEnumerable<PromptDefinition> prompts)
    {
        lock (_apiLock)
        {
            foreach (var prompt in prompts)
            {
                if (string.IsNullOrEmpty(prompt.Name) || string.IsNullOrEmpty(prompt.Domain))
                    continue;
                prompt.Source = "api";
                _apiIndex[MakeKey(prompt.Domain, prompt.Name)] = prompt;
            }
        }
        _logger.LogInformation("Bulk-registered {Count} prompts via API", _apiIndex.Count);
    }

    public bool Unregister(string domain, string name)
    {
        var key = MakeKey(domain, name);
        lock (_apiLock)
        {
            return _apiIndex.Remove(key);
        }
    }

    // ── Merged index ──────────────────────────────────────────────────────────

    /// <summary>
    /// Returns file-loaded + API-registered prompts merged. API takes precedence on collision.
    /// </summary>
    private Dictionary<string, PromptDefinition> MergedIndex()
    {
        Dictionary<string, PromptDefinition> apiSnapshot;
        lock (_apiLock)
        {
            if (_apiIndex.Count == 0)
                return _fileIndex;
            apiSnapshot = new Dictionary<string, PromptDefinition>(_apiIndex, StringComparer.Ordinal);
        }

        var merged = new Dictionary<string, PromptDefinition>(_fileIndex, StringComparer.Ordinal);
        foreach (var kv in apiSnapshot)
            merged[kv.Key] = kv.Value; // API overrides file
        return merged;
    }

    // ── Internal file loading ──────────────────────────────────────────────────

    private void LoadAll()
    {
        if (!Directory.Exists(_promptsRoot))
        {
            _logger.LogWarning("PromptsPath does not exist: {Path}", _promptsRoot);
            _fileIndex = [];
            return;
        }

        var next = new Dictionary<string, PromptDefinition>(StringComparer.Ordinal);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_promptsRoot, "*.yaml", SearchOption.AllDirectories).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            _logger.LogWarning("PromptsPath was removed before enumeration completed: {Path}", _promptsRoot);
            _fileIndex = [];
            return;
        }

        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file);
                var def  = _yaml.Deserialize<PromptDefinition>(text);
                if (string.IsNullOrEmpty(def.Name) || string.IsNullOrEmpty(def.Domain))
                {
                    _logger.LogWarning("Prompt file missing name/domain, skipping: {File}", file);
                    continue;
                }
                def.FilePath = file;
                def.Source = "file";
                next[MakeKey(def.Domain, def.Name)] = def;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load prompt YAML: {File}", file);
            }
        }

        _fileIndex = next;
        _logger.LogInformation("Prompt registry loaded {Count} prompts from {Path}", next.Count, _promptsRoot);
    }

    private static string MakeKey(string domain, string name) =>
        $"{domain.ToLowerInvariant()}/{name.ToLowerInvariant()}";

    public void Dispose() => _watcher.Dispose();
}
