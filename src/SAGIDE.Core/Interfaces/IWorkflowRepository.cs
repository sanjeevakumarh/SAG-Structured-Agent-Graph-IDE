using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

/// <summary>
/// Persistence contract for workflow instances.
/// Implemented by SqliteTaskRepository alongside ITaskRepository.
/// </summary>
public interface IWorkflowRepository
{
    Task SaveWorkflowInstanceAsync(WorkflowInstance instance);

    Task<IReadOnlyList<WorkflowInstance>> LoadRunningInstancesAsync();

    Task DeleteWorkflowInstanceAsync(string instanceId);
}
