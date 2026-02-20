using SAGIDE.Core.Models;

namespace SAGIDE.Core.DTOs;

public class TaskStatusResponse
{
    public string TaskId { get; set; } = string.Empty;
    public AgentTaskStatus Status { get; set; }
    public int Progress { get; set; }
    public string? StatusMessage { get; set; }
    public AgentType AgentType { get; set; }
    public ModelProvider ModelProvider { get; set; }
    public string ModelId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public AgentResult? Result { get; set; }
    public DateTime? ScheduledFor { get; set; }
    public string? ComparisonGroupId { get; set; }
}
