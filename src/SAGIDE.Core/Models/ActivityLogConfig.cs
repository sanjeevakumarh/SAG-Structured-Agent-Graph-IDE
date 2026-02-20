namespace SAGIDE.Core.Models;

public class ActivityLogConfig
{
    public string WorkspacePath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public GitIntegrationMode GitIntegrationMode { get; set; } = GitIntegrationMode.LogCommits;
    public bool MarkdownEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public enum GitIntegrationMode
{
    Disabled = 0,
    LogCommits = 1,
    GenerateMessages = 2,
    Bidirectional = 3
}
