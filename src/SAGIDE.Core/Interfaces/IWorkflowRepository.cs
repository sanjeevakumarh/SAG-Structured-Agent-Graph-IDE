using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Persistence contract for workflow instances.
/// Implemented by SqliteTaskRepository alongside ITaskRepository.
/// </summary>
public interface IWorkflowRepository
{
    /// <summary>Upsert a workflow instance (full JSON blob).</summary>
    Task SaveWorkflowInstanceAsync(WorkflowInstance instance);

    /// <summary>Load all instances that were Running or Paused when the service last stopped.</summary>
    Task<IReadOnlyList<WorkflowInstance>> LoadRunningInstancesAsync();

    /// <summary>Remove a completed/cancelled workflow from the persistence store.</summary>
    Task DeleteWorkflowInstanceAsync(string instanceId);
}
