using System.Collections.Concurrent;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Orchestrator;

public class TaskQueue
{
    private readonly ConcurrentDictionary<string, AgentTask> _allTasks = new();
    private readonly PriorityQueue<AgentTask, int> _pendingQueue = new();
    private readonly object _queueLock = new();

    public string Enqueue(AgentTask task)
    {
        _allTasks[task.Id] = task;
        lock (_queueLock)
        {
            // Lower number = higher priority
            _pendingQueue.Enqueue(task, -task.Priority);
        }
        return task.Id;
    }

    public AgentTask? Dequeue()
    {
        var (task, _) = DequeueOrGetDelay();
        return task;
    }

    /// <summary>
    /// Returns the highest-priority task that is ready to run now.
    /// If all pending tasks are scheduled for the future, returns (null, delay) where
    /// delay is the time until the next task becomes ready (capped at 1 minute).
    /// Returns (null, null) when the queue is empty.
    /// </summary>
    public (AgentTask? task, TimeSpan? retryAfter) DequeueOrGetDelay()
    {
        lock (_queueLock)
        {
            // Find the highest-priority task that is ready to run (ScheduledFor null or in the past)
            var ready = _pendingQueue.UnorderedItems
                .OrderBy(i => i.Priority)  // lower priority int = higher priority (negated on enqueue)
                .Select(i => i.Element)
                .FirstOrDefault(t => !t.ScheduledFor.HasValue || t.ScheduledFor.Value <= DateTime.UtcNow);

            if (ready != null)
            {
                _pendingQueue.Remove(ready, out _, out _);
                ready.Status = AgentTaskStatus.Running;
                ready.StartedAt = DateTime.UtcNow;
                return (ready, null);
            }

            // Nothing ready — find earliest scheduled task so caller knows when to retry
            var earliest = _pendingQueue.UnorderedItems
                .Select(i => i.Element)
                .Where(t => t.ScheduledFor.HasValue)
                .MinBy(t => t.ScheduledFor);

            if (earliest != null)
            {
                var delay = earliest.ScheduledFor!.Value - DateTime.UtcNow;
                return (null, delay < TimeSpan.FromMinutes(1) ? delay : TimeSpan.FromMinutes(1));
            }

            return (null, null); // queue empty
        }
    }

    public AgentTask? GetTask(string taskId)
    {
        _allTasks.TryGetValue(taskId, out var task);
        return task;
    }

    public IReadOnlyList<AgentTask> GetAllTasks()
    {
        return _allTasks.Values.OrderByDescending(t => t.CreatedAt).ToList();
    }

    public IReadOnlyList<AgentTask> GetRunningTasks()
    {
        return _allTasks.Values
            .Where(t => t.Status == AgentTaskStatus.Running)
            .ToList();
    }

    public int PendingCount
    {
        get { lock (_queueLock) { return _pendingQueue.Count; } }
    }

    public int RunningCount => _allTasks.Values.Count(t => t.Status == AgentTaskStatus.Running);

    public void UpdateTask(string taskId, Action<AgentTask> update)
    {
        if (_allTasks.TryGetValue(taskId, out var task))
        {
            update(task);
        }
    }
}
