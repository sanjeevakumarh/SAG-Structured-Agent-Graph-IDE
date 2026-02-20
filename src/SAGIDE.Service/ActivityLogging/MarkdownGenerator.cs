using System.Text;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.ActivityLogging;

public class MarkdownGenerator
{
    private readonly ILogger<MarkdownGenerator> _logger;

    public MarkdownGenerator(ILogger<MarkdownGenerator> logger)
    {
        _logger = logger;
    }

    public async Task GenerateHourlyLogAsync(
        string workspacePath,
        string hourBucket,
        List<ActivityEntry> activities,
        CancellationToken ct = default)
    {
        var logsDir = Path.Combine(workspacePath, ".sag-activity", "logs");
        Directory.CreateDirectory(logsDir);

        var logPath = Path.Combine(logsDir, $"{hourBucket}.md");

        var sb = new StringBuilder();

        // Header
        var hourTimestamp = ParseHourBucket(hourBucket);
        sb.AppendLine($"# Activity Log: {hourBucket}");
        sb.AppendLine();
        sb.AppendLine($"**Period:** {hourTimestamp:yyyy-MM-dd HH:00:00} - {hourTimestamp:yyyy-MM-dd HH:59:59}  ");
        sb.AppendLine($"**Total Activities:** {activities.Count}");
        sb.AppendLine();

        if (activities.Count == 0)
        {
            sb.AppendLine("_No activities recorded in this hour._");
            await File.WriteAllTextAsync(logPath, sb.ToString(), ct);
            return;
        }

        // Group by activity type
        var grouped = activities.GroupBy(a => a.ActivityType).OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            sb.AppendLine($"## {FormatActivityType(group.Key)} ({group.Count()})");
            sb.AppendLine();

            foreach (var activity in group.OrderBy(a => a.Timestamp))
            {
                sb.AppendLine($"### {activity.Timestamp:HH:mm:ss} - {activity.Summary}");
                sb.AppendLine();

                // Task ID reference
                if (!string.IsNullOrEmpty(activity.TaskId))
                {
                    sb.AppendLine($"**Task ID:** `{activity.TaskId}`  ");
                }

                // Git commit hash
                if (!string.IsNullOrEmpty(activity.GitCommitHash))
                {
                    sb.AppendLine($"**Commit:** `{activity.GitCommitHash[..8]}`  ");
                }

                // Files
                if (activity.FilePaths.Count > 0)
                {
                    sb.AppendLine("**Files:**");
                    foreach (var file in activity.FilePaths)
                    {
                        sb.AppendLine($"- `{file}`");
                    }
                    sb.AppendLine();
                }

                // Metadata
                if (activity.Metadata.Count > 0)
                {
                    sb.AppendLine("**Metadata:**");
                    foreach (var kvp in activity.Metadata.OrderBy(k => k.Key))
                    {
                        sb.AppendLine($"- **{kvp.Key}:** {kvp.Value}");
                    }
                    sb.AppendLine();
                }

                // Collapsible details
                if (!string.IsNullOrEmpty(activity.Details))
                {
                    sb.AppendLine("<details>");
                    sb.AppendLine("<summary>Details</summary>");
                    sb.AppendLine();
                    sb.AppendLine("```json");
                    sb.AppendLine(activity.Details);
                    sb.AppendLine("```");
                    sb.AppendLine("</details>");
                    sb.AppendLine();
                }

                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        await File.WriteAllTextAsync(logPath, sb.ToString(), ct);
        _logger.LogDebug("Generated hourly log: {LogPath}", logPath);
    }

    public async Task GenerateReadmeAsync(
        string workspacePath,
        List<string> hourBuckets,
        CancellationToken ct = default)
    {
        var readmePath = Path.Combine(workspacePath, ".sag-activity", "README.md");

        var sb = new StringBuilder();

        sb.AppendLine("# SAG IDE Activity Log");
        sb.AppendLine();
        sb.AppendLine("This directory contains hourly activity logs for all project activities tracked by SAG IDE.");
        sb.AppendLine();
        sb.AppendLine("## Table of Contents");
        sb.AppendLine();

        if (hourBuckets.Count == 0)
        {
            sb.AppendLine("_No activity logs yet. Start using SAG IDE to track your work!_");
            await File.WriteAllTextAsync(readmePath, sb.ToString(), ct);
            return;
        }

        // Group by date
        var byDate = hourBuckets
            .Select(h => ParseHourBucket(h))
            .GroupBy(dt => dt.Date)
            .OrderByDescending(g => g.Key);

        foreach (var dateGroup in byDate)
        {
            sb.AppendLine($"### {dateGroup.Key:yyyy-MM-dd}");
            sb.AppendLine();

            foreach (var hour in dateGroup.OrderByDescending(h => h))
            {
                var bucket = hour.ToString("yyyy-MM-dd-HH");
                sb.AppendLine($"- [{hour:HH:00}](logs/{bucket}.md)");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("_Generated by SAG IDE Activity Logger_");

        await File.WriteAllTextAsync(readmePath, sb.ToString(), ct);
        _logger.LogDebug("Generated activity README: {ReadmePath}", readmePath);
    }

    private static DateTime ParseHourBucket(string hourBucket)
    {
        // Format: YYYY-MM-DD-HH
        var parts = hourBucket.Split('-');
        if (parts.Length != 4)
        {
            throw new ArgumentException($"Invalid hour bucket format: {hourBucket}");
        }

        return new DateTime(
            int.Parse(parts[0]),  // Year
            int.Parse(parts[1]),  // Month
            int.Parse(parts[2]),  // Day
            int.Parse(parts[3]),  // Hour
            0, 0, DateTimeKind.Utc
        );
    }

    private static string FormatActivityType(ActivityType activityType)
    {
        return activityType switch
        {
            ActivityType.AgentTask => "🤖 Agent Tasks",
            ActivityType.HumanAction => "👤 Human Actions",
            ActivityType.GitCommit => "📝 Git Commits",
            ActivityType.FileModified => "📁 File Modifications",
            ActivityType.SystemEvent => "⚙️ System Events",
            _ => activityType.ToString()
        };
    }
}
