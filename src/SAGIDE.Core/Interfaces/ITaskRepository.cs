using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

public interface ITaskRepository
{
    Task InitializeAsync();
    Task SaveTaskAsync(AgentTask task);
    Task SaveResultAsync(AgentResult result);

    /// <summary>
    /// Atomically persists a completed/failed task status AND its result in a single transaction.
    /// Prevents the inconsistent state where a task is marked Completed but has no result row
    /// (or vice versa) if the process crashes between two separate calls.
    /// </summary>
    Task SaveTaskCompletedWithResultAsync(AgentTask task, AgentResult result);
    Task<AgentTask?> GetTaskAsync(string taskId);
    Task<AgentResult?> GetResultAsync(string taskId);
    Task<IReadOnlyList<AgentTask>> GetTaskHistoryAsync(int limit = 100, int offset = 0);
    Task<IReadOnlyList<AgentTask>> GetTasksByStatusAsync(AgentTaskStatus status);
    Task<IReadOnlyList<AgentTask>> GetTasksBySourceTagAsync(string sourceTag, int limit = 100, int offset = 0);
    Task<IReadOnlyList<AgentTask>> LoadPendingTasksAsync();
    Task SaveDlqEntryAsync(DeadLetterEntry entry);
    Task<IReadOnlyList<DeadLetterEntry>> GetDlqEntriesAsync();
    Task RemoveDlqEntryAsync(string dlqId);
    Task PurgeDlqOlderThanAsync(DateTime cutoff);

    // Determinism & Replay — output cache
    Task<string?> GetCachedOutputAsync(string cacheKey);
    Task StoreCachedOutputAsync(string cacheKey, string output, string modelId);

    /// <summary>Executes a lightweight <c>SELECT 1</c> to verify the database is reachable.</summary>
    Task<bool> CheckHealthAsync(CancellationToken ct = default);
}
