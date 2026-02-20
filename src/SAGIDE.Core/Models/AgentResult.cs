namespace SAGIDE.Core.Models;

public class AgentResult
{
    public string TaskId { get; set; } = string.Empty;
    public bool Success { get; set; }
    public string Output { get; set; } = string.Empty;
    public List<Issue> Issues { get; set; } = [];
    public List<FileChange> Changes { get; set; } = [];
    public int TokensUsed { get; set; }
    public double EstimatedCost { get; set; }
    public long LatencyMs { get; set; }
    public string? ErrorMessage { get; set; }

    public static AgentResult Failed(string taskId, string error) => new()
    {
        TaskId = taskId,
        Success = false,
        ErrorMessage = error
    };
}

public class Issue
{
    public string FilePath { get; set; } = string.Empty;
    public int Line { get; set; }
    public IssueSeverity Severity { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? SuggestedFix { get; set; }
}

public enum IssueSeverity { Info, Low, Medium, High, Critical }

public class FileChange
{
    public string FilePath { get; set; } = string.Empty;
    public string OriginalContent { get; set; } = string.Empty;
    public string NewContent { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}
