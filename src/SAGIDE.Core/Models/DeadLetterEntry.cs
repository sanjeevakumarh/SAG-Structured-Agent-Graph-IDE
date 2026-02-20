namespace SAGIDE.Core.Models;

public class DeadLetterEntry
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N")[..12];
    public string OriginalTaskId { get; init; } = string.Empty;
    public AgentType AgentType { get; init; }
    public ModelProvider ModelProvider { get; init; }
    public string ModelId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public List<string> FilePaths { get; init; } = [];
    public string ErrorMessage { get; init; } = string.Empty;
    public string? ErrorCode { get; init; }
    public int RetryCount { get; init; }
    public DateTime FailedAt { get; init; } = DateTime.UtcNow;
    public DateTime OriginalCreatedAt { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
}
