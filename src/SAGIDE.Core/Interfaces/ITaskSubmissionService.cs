using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Abstracts task submission and cancellation so WorkflowEngine can depend on this
/// interface instead of the concrete AgentOrchestrator, breaking the circular dependency.
/// </summary>
public interface ITaskSubmissionService
{
    /// <summary>Enqueues a task for agent execution and returns the assigned task ID.</summary>
    Task<string> SubmitTaskAsync(AgentTask task, CancellationToken ct);

    /// <summary>Requests cancellation of an in-flight or queued task.</summary>
    Task CancelTaskAsync(string taskId, CancellationToken ct);
}
