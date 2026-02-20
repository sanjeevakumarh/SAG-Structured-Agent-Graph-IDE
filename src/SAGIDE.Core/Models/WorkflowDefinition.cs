namespace SAGIDE.Core.Models;


public class WorkflowDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<WorkflowParameter> Parameters { get; set; } = [];
    public List<WorkflowStepDef> Steps { get; set; } = [];
    public bool IsBuiltIn { get; set; }
}

public class WorkflowParameter
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "string";
    public string? Default { get; set; }
}

public class WorkflowStepDef
{
    /// <summary>Unique step identifier within this workflow (e.g. "code_review").</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>"agent" (default) or "router" (conditional branching, no task submitted).</summary>
    public string Type { get; set; } = "agent";

    /// <summary>Agent name mapped to AgentType enum: Coder, Reviewer, Tester, Security, Documenter, Debug.</summary>
    public string? Agent { get; set; }

    /// <summary>Step IDs that must complete before this step runs.</summary>
    public List<string> DependsOn { get; set; } = [];

    /// <summary>Prompt template; supports {{param_name}} and {{step_id.output}} substitution.</summary>
    public string? Prompt { get; set; }

    /// <summary>Override model ID for this step (e.g. "claude-sonnet-4-6").</summary>
    public string? ModelId { get; set; }

    /// <summary>Override model provider for this step (e.g. "claude", "ollama").</summary>
    public string? ModelProvider { get; set; }

    /// <summary>For feedback loops: step ID to re-run after this step completes (if issues found).</summary>
    public string? Next { get; set; }

    /// <summary>Maximum number of loop iterations when Next is set. Default 1 (no loop).</summary>
    public int MaxIterations { get; set; } = 1;

    /// <summary>Router configuration — only used when Type == "router".</summary>
    public RouterConfig? Router { get; set; }

    // ── Tool step fields (Type == "tool") ─────────────────────────────────────

    /// <summary>Shell command to run, e.g. "dotnet build" or "npm test".</summary>
    public string? Command { get; set; }

    /// <summary>Working directory for the command. Defaults to the workflow workspace path.</summary>
    public string? WorkingDir { get; set; }

    /// <summary>How non-zero exit codes are handled: FAIL_ON_NONZERO | WARN_ON_NONZERO | IGNORE.</summary>
    public string ExitCodePolicy { get; set; } = "FAIL_ON_NONZERO";

    // ── Constraint step fields (Type == "constraint") ─────────────────────────

    /// <summary>
    /// Constraint expression to evaluate against prior step outputs. Examples:
    ///   exit_code(build) == 0
    ///   output(review).contains('PASS')
    ///   issue_count(review) == 0
    /// </summary>
    public string? ConstraintExpr { get; set; }

    /// <summary>What to do when the constraint fails: "fail" (default) or "warn".</summary>
    public string OnConstraintFail { get; set; } = "fail";
}

public class RouterConfig
{
    /// <summary>Evaluated in order; first matching branch wins.</summary>
    public List<RouterBranch> Branches { get; set; } = [];
}

public class RouterBranch
{
    /// <summary>Condition expression: "hasIssues", "success", "failed", or "output.contains('X')".</summary>
    public string Condition { get; set; } = string.Empty;

    /// <summary>Step ID to activate when this condition is true.</summary>
    public string Target { get; set; } = string.Empty;
}
