using System.Collections.Concurrent;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Orchestrator;

public class TaskQueue
{
    private readonly ConcurrentDictionary<string, AgentTask> _allTasks = new();
    private readonly object _queueLock = new();

    // Dual-heap dequeue — O(log n) instead of the prior O(n log n) OrderBy scan.
    //   _readyQueue    — tasks whose ScheduledFor is null or already past, keyed by -Priority
    //                    (PriorityQueue is a min-heap, so -Priority gives highest-priority-first).
    //   _scheduledQueue — tasks whose ScheduledFor is still in the future, keyed by ScheduledFor.Ticks
    //                     (min-heap = earliest-due-first, enabling O(log n) promotion).
    private readonly PriorityQueue<AgentTask, int>  _readyQueue     = new();
    private readonly PriorityQueue<AgentTask, long> _scheduledQueue = new();

    // ── Bounded in-memory history ─────────────────────────────────────────────
    // Terminal tasks are candidates for eviction; active tasks are never evicted.
    // _terminalOrder tracks insertion order for FIFO eviction.
    // Complete task history is always available in SQLite.
    private readonly Queue<string> _terminalOrder = new();
    private readonly int _maxHistorySize;

    public TaskQueue(int maxHistorySize = 1000)
    {
        _maxHistorySize = maxHistorySize;
    }

    public string Enqueue(AgentTask task)
    {
        _allTasks[task.Id] = task;
        lock (_queueLock)
        {
            if (!task.ScheduledFor.HasValue || task.ScheduledFor.Value <= DateTime.UtcNow)
                _readyQueue.Enqueue(task, -task.Priority);
            else
                _scheduledQueue.Enqueue(task, task.ScheduledFor.Value.Ticks);
        }
        return task.Id;
    }

    /// <summary>
    /// Called when a task reaches a terminal status (Completed, Failed, Cancelled).
    /// Registers the task for FIFO eviction once the in-memory history exceeds the cap.
    /// Full task history remains available in SQLite.
    /// </summary>
    public void MarkTerminal(string taskId)
    {
        lock (_queueLock)
        {
            _terminalOrder.Enqueue(taskId);
            while (_allTasks.Count > _maxHistorySize && _terminalOrder.TryDequeue(out var evictId))
            {
                if (_allTasks.TryGetValue(evictId, out var t) &&
                    t.Status is AgentTaskStatus.Completed or AgentTaskStatus.Failed or AgentTaskStatus.Cancelled)
                    _allTasks.TryRemove(evictId, out _);
            }
        }
    }

    public AgentTask? Dequeue()
    {
        var (task, _) = DequeueOrGetDelay();
        return task;
    }

    /// <summary>
    /// Returns the highest-priority task that is ready to run now (O(log n)).
    /// If all pending tasks are scheduled for the future, returns (null, delay) where
    /// delay is the time until the next task becomes ready (capped at 1 minute).
    /// Returns (null, null) when both heaps are empty.
    /// </summary>
    public (AgentTask? task, TimeSpan? retryAfter) DequeueOrGetDelay()
    {
        lock (_queueLock)
        {
            PromoteScheduledTasks();

            // Dequeue highest-priority ready task, skipping any that were cancelled while queued.
            while (_readyQueue.TryDequeue(out var candidate, out _))
            {
                if (candidate.Status == AgentTaskStatus.Cancelled)
                    continue; // already cancelled — discard without executing
                candidate.Status    = AgentTaskStatus.Running;
                candidate.StartedAt = DateTime.UtcNow;
                return (candidate, null);
            }

            // Nothing ready — report delay until next scheduled task
            if (_scheduledQueue.TryPeek(out _, out var earliestTicks))
            {
                var delay = TimeSpan.FromTicks(earliestTicks - DateTime.UtcNow.Ticks);
                if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
                return (null, delay < TimeSpan.FromMinutes(1) ? delay : TimeSpan.FromMinutes(1));
            }

            return (null, null); // both heaps empty
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
        get { lock (_queueLock) { return _readyQueue.Count + _scheduledQueue.Count; } }
    }

    public int RunningCount => _allTasks.Values.Count(t => t.Status == AgentTaskStatus.Running);

    public void UpdateTask(string taskId, Action<AgentTask> update)
    {
        if (_allTasks.TryGetValue(taskId, out var task))
        {
            update(task);
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Moves any scheduled tasks whose due time has arrived into the ready heap.
    /// O(k log n) where k is the number of tasks promoted — typically 0.
    /// Must be called under <see cref="_queueLock"/>.
    /// </summary>
    private void PromoteScheduledTasks()
    {
        var nowTicks = DateTime.UtcNow.Ticks;
        while (_scheduledQueue.TryPeek(out _, out var ticks) && ticks <= nowTicks)
        {
            var task = _scheduledQueue.Dequeue();
            _readyQueue.Enqueue(task, -task.Priority);
        }
    }
}
