namespace SAGIDE.Core.Models;

public class ActivityEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string WorkspacePath { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string HourBucket { get; set; } = string.Empty;  // YYYY-MM-DD-HH
    public ActivityType ActivityType { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string? Details { get; set; }
    public string? TaskId { get; set; }
    public List<string> FilePaths { get; set; } = new();
    public string? GitCommitHash { get; set; }
    public Dictionary<string, string> Metadata { get; set; } = new();
}

public enum ActivityType
{
    AgentTask = 0,
    HumanAction = 1,
    GitCommit = 2,
    FileModified = 3,
    SystemEvent = 4
}
