using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SAGIDE.Contracts;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SAGIDE.Service.Prompts;

/// <summary>
/// Loads all skill YAML files from the configured SkillsPath directory, indexes them by
/// (domain, name), and hot-reloads when files change on disk.
/// Also supports API-based registration for external applications.
/// </summary>
public sealed class SkillRegistry : ISkillRegistry, ISkillRegistrationService, IDisposable
{
    private readonly string _skillsRoot;
    private readonly ILogger<SkillRegistry> _logger;
    private readonly IDeserializer _yaml;
    private readonly FileSystemWatcher _watcher;

    // File-loaded skills — rebuilt on every file change
    private volatile Dictionary<string, SkillDefinition> _fileIndex = [];

    // API-registered skills — survive file reloads
    private readonly Dictionary<string, SkillDefinition> _apiIndex = new(StringComparer.Ordinal);
    private readonly object _apiLock = new();

    /// <summary>
    /// Shared text blocks loaded from <c>skills/shared/prompt-blocks.yaml</c>.
    /// Available in skill templates as <c>{{blocks.block_name}}</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string> PromptBlocks { get; private set; }
        = new Dictionary<string, string>();

    public SkillRegistry(IConfiguration configuration, IHostEnvironment env, ILogger<SkillRegistry> logger)
    {
        _logger = logger;

        var configuredPath = configuration["SAGIDE:SkillsPath"] ?? "../../skills";
        _skillsRoot = Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(env.ContentRootPath, configuredPath));

        // If the resolved path doesn't exist, walk up from the exe location to
        // find the repo-root skills/ directory automatically.
        if (!Directory.Exists(_skillsRoot))
        {
            var discovered = WalkUpForSkillsDir(AppContext.BaseDirectory);
            if (discovered is not null)
            {
                _logger.LogInformation(
                    "SkillsPath '{Configured}' not found; auto-discovered skills directory: {Discovered}",
                    _skillsRoot, discovered);
                _skillsRoot = discovered;
            }
        }

        _yaml = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        LoadAll();

        if (Directory.Exists(_skillsRoot))
        {
            _watcher = new FileSystemWatcher(_skillsRoot, "*.yaml")
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
            _logger.LogWarning("SkillsPath does not exist: {Path} — skill library will be empty", _skillsRoot);
            _watcher = new FileSystemWatcher { EnableRaisingEvents = false };
        }
    }

    // ── ISkillRegistry (read) ─────────────────────────────────────────────────

    public IReadOnlyList<SkillDefinition> GetAll() => [.. MergedIndex().Values];

    public IReadOnlyList<SkillDefinition> GetByDomain(string domain)
    {
        var prefix = domain.ToLowerInvariant() + "/";
        return [.. MergedIndex()
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(kv => kv.Value)];
    }

    public SkillDefinition? GetByKey(string domain, string name) =>
        MergedIndex().TryGetValue(MakeKey(domain, name), out var def) ? def : null;

    /// <summary>
    /// Resolves a skill reference that is either "domain/name" or just "name".
    /// When only a name is given, searches all domains and returns the first match.
    /// Returns null if not found; logs a warning.
    /// </summary>
    public SkillDefinition? Resolve(string skillRef)
    {
        var merged = MergedIndex();

        // Fully-qualified: "research/web-research-track"
        if (skillRef.Contains('/'))
        {
            var slash = skillRef.IndexOf('/');
            var domain = skillRef[..slash].Trim();
            var name   = skillRef[(slash + 1)..].Trim();
            var key    = MakeKey(domain, name);
            if (merged.TryGetValue(key, out var result))
                return result;
            _logger.LogWarning("Skill '{Ref}' not found in registry", skillRef);
            return null;
        }

        // Short name: search all domains
        var lower = skillRef.ToLowerInvariant();
        var match = merged.Values.FirstOrDefault(s => s.Name.ToLowerInvariant() == lower);
        if (match is null)
            _logger.LogWarning("Skill '{Ref}' not found in registry (searched all domains)", skillRef);
        return match;
    }

    // ── ISkillRegistrationService (write) ─────────────────────────────────────

    public void Register(SkillDefinition skill)
    {
        if (string.IsNullOrEmpty(skill.Name) || string.IsNullOrEmpty(skill.Domain))
            throw new ArgumentException("Skill must have both name and domain set.");

        skill.Source = "api";
        var key = MakeKey(skill.Domain, skill.Name);
        lock (_apiLock)
        {
            _apiIndex[key] = skill;
        }
        _logger.LogInformation("API-registered skill: {Domain}/{Name} v{Version}", skill.Domain, skill.Name, skill.Version);
    }

    public void RegisterBulk(IEnumerable<SkillDefinition> skills)
    {
        lock (_apiLock)
        {
            foreach (var skill in skills)
            {
                if (string.IsNullOrEmpty(skill.Name) || string.IsNullOrEmpty(skill.Domain))
                    continue;
                skill.Source = "api";
                _apiIndex[MakeKey(skill.Domain, skill.Name)] = skill;
            }
        }
        _logger.LogInformation("Bulk-registered {Count} skills via API", _apiIndex.Count);
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
    /// Returns file-loaded + API-registered skills merged. API takes precedence on collision.
    /// </summary>
    private Dictionary<string, SkillDefinition> MergedIndex()
    {
        Dictionary<string, SkillDefinition> apiSnapshot;
        lock (_apiLock)
        {
            if (_apiIndex.Count == 0)
                return _fileIndex;
            apiSnapshot = new Dictionary<string, SkillDefinition>(_apiIndex, StringComparer.Ordinal);
        }

        var merged = new Dictionary<string, SkillDefinition>(_fileIndex, StringComparer.Ordinal);
        foreach (var kv in apiSnapshot)
            merged[kv.Key] = kv.Value; // API overrides file
        return merged;
    }

    // ── Internal file loading ────────────────────────────────────────────────

    private void LoadAll()
    {
        if (!Directory.Exists(_skillsRoot))
        {
            _fileIndex = [];
            return;
        }

        var next = new Dictionary<string, SkillDefinition>(StringComparer.Ordinal);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(_skillsRoot, "*.yaml", SearchOption.AllDirectories).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            _fileIndex = [];
            return;
        }

        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file);
                var def  = _yaml.Deserialize<SkillDefinition>(text);
                if (string.IsNullOrEmpty(def.Name) || string.IsNullOrEmpty(def.Domain))
                {
                    _logger.LogWarning("Skill file missing name/domain, skipping: {File}", file);
                    continue;
                }
                // prompt-blocks.yaml is a block library, not a real skill — skip indexing
                if (def.Name.Equals("prompt-blocks", StringComparison.OrdinalIgnoreCase))
                    continue;

                def.FilePath = file;
                def.Source = "file";
                next[MakeKey(def.Domain, def.Name)] = def;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load skill YAML: {File}", file);
            }
        }

        _fileIndex = next;

        // ── Load prompt blocks from the well-known file ──────────────────────
        LoadPromptBlocks();

        _logger.LogInformation("Skill registry loaded {Count} skills from {Path}", next.Count, _skillsRoot);
    }

    private void LoadPromptBlocks()
    {
        var blocksFile = Path.Combine(_skillsRoot, "shared.prompt-blocks.yaml");
        if (!File.Exists(blocksFile))
            blocksFile = Path.Combine(_skillsRoot, "shared", "prompt-blocks.yaml");
        if (!File.Exists(blocksFile))
        {
            PromptBlocks = new Dictionary<string, string>();
            return;
        }

        try
        {
            var text = File.ReadAllText(blocksFile);
            var raw  = _yaml.Deserialize<Dictionary<string, object>>(text);
            if (raw is not null
                && raw.TryGetValue("blocks", out var blocksObj)
                && blocksObj is Dictionary<object, object> dict)
            {
                var blocks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kv in dict)
                {
                    if (kv.Key is string key && kv.Value is string val)
                        blocks[key] = val;
                }
                PromptBlocks = blocks;
                _logger.LogInformation("Loaded {Count} prompt blocks from {File}", blocks.Count, blocksFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load prompt blocks from {File}", blocksFile);
        }
    }

    private static string MakeKey(string domain, string name) =>
        $"{domain.ToLowerInvariant()}/{name.ToLowerInvariant()}";

    /// <summary>
    /// Walks up from <paramref name="startDir"/> looking for a <c>skills/</c> subdirectory.
    /// </summary>
    private static string? WalkUpForSkillsDir(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "skills");
            if (Directory.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        return null;
    }

    public void Dispose() => _watcher.Dispose();
}
