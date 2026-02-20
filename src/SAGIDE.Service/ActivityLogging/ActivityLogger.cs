using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.ActivityLogging;

public class ActivityLogger
{
    private readonly IActivityRepository _repository;
    private readonly MarkdownGenerator _markdownGenerator;
    private readonly ILogger<ActivityLogger> _logger;
    private readonly ConcurrentDictionary<string, DateTime> _lastMarkdownGeneration = new();
    private readonly TimeSpan _markdownDebounce = TimeSpan.FromMinutes(1);

    public ActivityLogger(
        IActivityRepository repository,
        MarkdownGenerator markdownGenerator,
        ILogger<ActivityLogger> logger)
    {
        _repository = repository;
        _markdownGenerator = markdownGenerator;
        _logger = logger;
    }

    public async Task InitializeWorkspaceAsync(string workspacePath, CancellationToken ct = default)
    {
        // Create .sag-activity directory
        var activityDir = Path.Combine(workspacePath, ".sag-activity");
        var logsDir = Path.Combine(activityDir, "logs");

        Directory.CreateDirectory(logsDir);

        // Create default config if it doesn't exist
        var existingConfig = await _repository.GetConfigAsync(workspacePath);
        if (existingConfig == null)
        {
            var config = new ActivityLogConfig
            {
                WorkspacePath = workspacePath,
                Enabled = true,
                GitIntegrationMode = GitIntegrationMode.LogCommits,
                MarkdownEnabled = true,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _repository.SaveConfigAsync(config);
            _logger.LogInformation("Initialized activity logging for workspace: {WorkspacePath}", workspacePath);
        }

        // Generate initial README.md
        await GenerateReadmeAsync(workspacePath, ct);
    }

    public async Task<string> LogActivityAsync(ActivityEntry entry, CancellationToken ct = default)
    {
        // Check if logging is enabled for this workspace
        var config = await _repository.GetConfigAsync(entry.WorkspacePath);
        if (config == null || !config.Enabled)
        {
            return entry.Id;
        }

        // Set hour bucket if not already set
        if (string.IsNullOrEmpty(entry.HourBucket))
        {
            entry.HourBucket = entry.Timestamp.ToString("yyyy-MM-dd-HH");
        }

        // Save to database
        await _repository.SaveActivityAsync(entry);

        _logger.LogDebug("Logged activity {ActivityId}: {Summary}", entry.Id, entry.Summary);

        // Trigger markdown generation (debounced)
        if (config.MarkdownEnabled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await GenerateMarkdownForHourAsync(entry.WorkspacePath, entry.HourBucket, ct);
                    await GenerateReadmeAsync(entry.WorkspacePath, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to generate markdown for activity {ActivityId}", entry.Id);
                }
            }, ct);
        }

        return entry.Id;
    }

    public async Task<List<ActivityEntry>> GetActivitiesByHourAsync(
        string workspacePath,
        string hourBucket,
        CancellationToken ct = default)
    {
        var activities = await _repository.GetActivitiesByHourAsync(workspacePath, hourBucket);
        return activities.ToList();
    }

    public async Task<List<string>> GetHourBucketsAsync(
        string workspacePath,
        int limit = 100,
        CancellationToken ct = default)
    {
        var buckets = await _repository.GetHourBucketsAsync(workspacePath, limit);
        return buckets.ToList();
    }

    public async Task<ActivityLogConfig?> GetConfigAsync(string workspacePath, CancellationToken ct = default)
    {
        return await _repository.GetConfigAsync(workspacePath);
    }

    public async Task UpdateConfigAsync(ActivityLogConfig config, CancellationToken ct = default)
    {
        config.UpdatedAt = DateTime.UtcNow;
        await _repository.SaveConfigAsync(config);
        _logger.LogInformation("Updated activity log config for workspace: {WorkspacePath}", config.WorkspacePath);
    }

    public async Task GenerateMarkdownForHourAsync(
        string workspacePath,
        string hourBucket,
        CancellationToken ct = default)
    {
        // Debounce: Don't regenerate if we did it recently
        var key = $"{workspacePath}:{hourBucket}";
        if (_lastMarkdownGeneration.TryGetValue(key, out var lastGen))
        {
            if (DateTime.UtcNow - lastGen < _markdownDebounce)
            {
                return;
            }
        }

        var activities = await _repository.GetActivitiesByHourAsync(workspacePath, hourBucket);
        await _markdownGenerator.GenerateHourlyLogAsync(workspacePath, hourBucket, activities.ToList(), ct);

        _lastMarkdownGeneration[key] = DateTime.UtcNow;
    }

    public async Task GenerateReadmeAsync(string workspacePath, CancellationToken ct = default)
    {
        // Debounce README generation
        var key = $"{workspacePath}:readme";
        if (_lastMarkdownGeneration.TryGetValue(key, out var lastGen))
        {
            if (DateTime.UtcNow - lastGen < _markdownDebounce)
            {
                return;
            }
        }

        var hourBuckets = await _repository.GetHourBucketsAsync(workspacePath, 100);
        await _markdownGenerator.GenerateReadmeAsync(workspacePath, hourBuckets.ToList(), ct);

        _lastMarkdownGeneration[key] = DateTime.UtcNow;
    }

    public async Task<List<ActivityEntry>> GetActivitiesByTimeRangeAsync(
        string workspacePath,
        DateTime start,
        DateTime end,
        CancellationToken ct = default)
    {
        var activities = await _repository.GetActivitiesByTimeRangeAsync(workspacePath, start, end);
        return activities.ToList();
    }

    public static string GetHourBucket(DateTime timestamp)
    {
        return timestamp.ToString("yyyy-MM-dd-HH");
    }
}
