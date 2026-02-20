namespace SAGIDE.Core.Models;

public enum AgentTaskStatus
{
    Queued,
    Running,
    WaitingApproval,
    Completed,
    Failed,
    Cancelled
}
