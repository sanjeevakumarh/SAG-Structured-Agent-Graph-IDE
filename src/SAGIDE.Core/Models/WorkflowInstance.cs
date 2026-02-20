namespace SAGIDE.Core.Models;

/// <summary>
/// Runtime state of a running (or completed) workflow instance.
/// Persisted to SQLite so state survives service restarts.
/// </summary>
public class WorkflowInstance
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string DefinitionId { get; set; } = string.Empty;
    public string DefinitionName { get; set; } = string.Empty;
    public WorkflowStatus Status { get; set; } = WorkflowStatus.Running;

    /// <summary>User-supplied parameter values (maps {{param_name}} → value).</summary>
    public Dictionary<string, string> InputContext { get; set; } = [];

    /// <summary>Per-step execution state, keyed by step ID.</summary>
    public Dictionary<string, WorkflowStepExecution> StepExecutions { get; set; } = [];

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public List<string> FilePaths { get; set; } = [];
    public string DefaultModelId { get; set; } = string.Empty;
    public string DefaultModelProvider { get; set; } = string.Empty;
    public string? ModelEndpoint { get; set; }

    /// <summary>
    /// When true, running tasks are allowed to complete but no new steps are submitted.
    /// Call WorkflowEngine.ResumeAsync to re-evaluate pending steps and continue.
    /// </summary>
    public bool IsPaused { get; set; }

    /// <summary>Workspace path stored so the instance can be recovered from DB after restart.</summary>
    public string? WorkspacePath { get; set; }
}

public class WorkflowStepExecution
{
    public string StepId { get; set; } = string.Empty;

    /// <summary>The AgentTask ID submitted for this step (null if not yet started).</summary>
    public string? TaskId { get; set; }

    public WorkflowStepStatus Status { get; set; } = WorkflowStepStatus.Pending;

    /// <summary>Raw LLM output from the completed step.</summary>
    public string? Output { get; set; }

    public int IssueCount { get; set; }

    /// <summary>1-based iteration count for feedback-loop steps.</summary>
    public int Iteration { get; set; } = 1;

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? Error { get; set; }

    /// <summary>Exit code from a tool step execution. Null for agent / router / constraint steps.</summary>
    public int? ExitCode { get; set; }
}

public enum WorkflowStatus
{
    Running,
    Completed,
    Failed,
    Cancelled,
    Paused,
}

public enum WorkflowStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
}
