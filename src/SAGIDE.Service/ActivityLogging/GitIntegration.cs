using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.ActivityLogging;

public class GitIntegration
{
    private readonly ActivityLogger _activityLogger;
    private readonly ILogger<GitIntegration> _logger;

    public GitIntegration(ActivityLogger activityLogger, ILogger<GitIntegration> logger)
    {
        _activityLogger = activityLogger;
        _logger = logger;
    }

    public async Task SyncFromGitHistoryAsync(
        string workspacePath,
        DateTime? since = null,
        CancellationToken ct = default)
    {
        var gitDir = Path.Combine(workspacePath, ".git");
        if (!Directory.Exists(gitDir))
        {
            _logger.LogWarning("No .git directory found in workspace: {WorkspacePath}", workspacePath);
            return;
        }

        // Build git log command
        var sinceArg = since.HasValue ? $"--since=\"{since.Value:yyyy-MM-dd}\"" : "--all";

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"log {sinceArg} --pretty=format:\"%H|%aI|%an|%s\" --name-only",
                WorkingDirectory = workspacePath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            var error = await process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            if (process.ExitCode != 0)
            {
                _logger.LogError("Git log failed: {Error}", error);
                return;
            }

            // Parse git log output
            var commits = ParseGitLog(output);

            _logger.LogInformation("Found {Count} commits to sync from git history", commits.Count);

            // Create activity entries for each commit
            foreach (var commit in commits)
            {
                var entry = new ActivityEntry
                {
                    WorkspacePath = workspacePath,
                    Timestamp = commit.Timestamp,
                    HourBucket = ActivityLogger.GetHourBucket(commit.Timestamp),
                    ActivityType = ActivityType.GitCommit,
                    Actor = "git",
                    Summary = $"Commit by {commit.Author}: {commit.Message}",
                    GitCommitHash = commit.Hash,
                    FilePaths = commit.Files,
                    Metadata = new Dictionary<string, string>
                    {
                        ["author"] = commit.Author,
                        ["commitMessage"] = commit.Message,
                        ["commitHash"] = commit.Hash
                    }
                };

                await _activityLogger.LogActivityAsync(entry, ct);
            }

            _logger.LogInformation("Synced {Count} git commits to activity log", commits.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync git history for workspace: {WorkspacePath}", workspacePath);
        }
    }

    public async Task<string> GenerateCommitMessageAsync(
        string workspacePath,
        DateTime? since = null,
        CancellationToken ct = default)
    {
        var config = await _activityLogger.GetConfigAsync(workspacePath, ct);
        if (config == null)
        {
            config = new ActivityLogConfig { WorkspacePath = workspacePath };
        }

        // Default to last hour if not specified
        since ??= DateTime.UtcNow.AddHours(-1);

        var activities = await _activityLogger.GetActivitiesByTimeRangeAsync(
            workspacePath,
            since.Value,
            DateTime.UtcNow,
            ct);

        var sb = new StringBuilder();

        // Commit message prefix
        sb.AppendLine($"[SAG-IDE] Activity summary {since.Value:yyyy-MM-dd HH:mm} - {DateTime.UtcNow:HH:mm}");
        sb.AppendLine();

        // Agent tasks
        var agentTasks = activities.Where(a => a.ActivityType == ActivityType.AgentTask).ToList();
        if (agentTasks.Count > 0)
        {
            sb.AppendLine("Agent Tasks:");
            foreach (var task in agentTasks.Take(5))
            {
                var status = task.Metadata.GetValueOrDefault("status", "unknown");
                sb.AppendLine($"- {task.Summary} ({status})");
            }
            if (agentTasks.Count > 5)
            {
                sb.AppendLine($"- ... and {agentTasks.Count - 5} more");
            }
            sb.AppendLine();
        }

        // Human actions
        var humanActions = activities.Where(a => a.ActivityType == ActivityType.HumanAction).ToList();
        if (humanActions.Count > 0)
        {
            sb.AppendLine($"Human Actions: {humanActions.Count}");
            sb.AppendLine();
        }

        // File changes
        var fileChanges = activities.SelectMany(a => a.FilePaths).Distinct().ToList();
        if (fileChanges.Count > 0)
        {
            sb.AppendLine($"Files modified: {fileChanges.Count}");
            if (fileChanges.Count <= 10)
            {
                foreach (var file in fileChanges)
                {
                    sb.AppendLine($"- {file}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine("Co-Authored-By: SAG IDE <noreply@sag-ide.com>");

        return sb.ToString();
    }

    private List<GitCommitInfo> ParseGitLog(string output)
    {
        var commits = new List<GitCommitInfo>();

        if (string.IsNullOrWhiteSpace(output))
        {
            return commits;
        }

        var currentCommit = (GitCommitInfo?)null;
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            // Check if this is a commit line (format: hash|timestamp|author|message)
            if (line.Contains('|'))
            {
                // Save previous commit if exists
                if (currentCommit != null)
                {
                    commits.Add(currentCommit);
                }

                // Parse new commit
                var parts = line.Split('|', 4);
                if (parts.Length >= 4)
                {
                    currentCommit = new GitCommitInfo
                    {
                        Hash = parts[0],
                        Timestamp = DateTime.Parse(parts[1]),
                        Author = parts[2],
                        Message = parts[3],
                        Files = new List<string>()
                    };
                }
            }
            else if (currentCommit != null && !string.IsNullOrWhiteSpace(line))
            {
                // This is a file path
                currentCommit.Files.Add(line.Trim());
            }
        }

        // Add the last commit
        if (currentCommit != null)
        {
            commits.Add(currentCommit);
        }

        return commits;
    }

    private class GitCommitInfo
    {
        public string Hash { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string Author { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public List<string> Files { get; set; } = new();
    }
}
