using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.DTOs;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// DAG-based workflow execution engine.
///
/// Responsibilities:
///   - Sequential and parallel step submission via AgentOrchestrator
///   - Conditional routing (router nodes evaluated synchronously)
///   - Feedback loops (next: back-edges) capped by both YAML max_iterations and
///     the global AgentLimits:MaxIterations configuration value
///   - Context passing via {{step_id.output}} template variables
///   - Per-step policy enforcement (protected files, blocked agent types)
///   - Smart Router: falls back to TaskAffinities when no model is explicitly specified
///   - Pause/resume without losing pending steps
///   - Live context variable updates while the workflow is running
///   - SQLite persistence: instances survive service restarts
///
/// Cancel behaviour (Item 2):
///   CancelAsync() calls AgentOrchestrator.CancelTaskAsync() for every task that has
///   been submitted (whether it is still Queued or actively Running in the orchestrator),
///   then marks all remaining Pending steps as Skipped.
/// </summary>
public class WorkflowEngine
{
    private readonly AgentOrchestrator _orchestrator;
    private readonly WorkflowDefinitionLoader _loader;
    private readonly AgentLimitsConfig _agentLimitsConfig;
    private readonly TaskAffinitiesConfig _taskAffinitiesConfig;
    private readonly WorkflowPolicyEngine _policyEngine;
    private readonly IWorkflowRepository? _workflowRepository;
    private readonly ILogger<WorkflowEngine> _logger;

    // instanceId → (instance, definition)
    private readonly ConcurrentDictionary<string, (WorkflowInstance Inst, WorkflowDefinition Def)> _active = new();

    // taskId → (instanceId, stepId) — reverse lookup for OnTaskUpdateAsync
    private readonly ConcurrentDictionary<string, (string InstanceId, string StepId)> _taskToStep = new();

    // Per-instance semaphore to serialise DAG evaluation (prevents races when parallel steps complete)
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

    public event Action<WorkflowInstance>? OnWorkflowUpdate;

    public WorkflowEngine(
        AgentOrchestrator orchestrator,
        WorkflowDefinitionLoader loader,
        AgentLimitsConfig agentLimitsConfig,
        TaskAffinitiesConfig taskAffinitiesConfig,
        WorkflowPolicyEngine policyEngine,
        ILogger<WorkflowEngine> logger,
        IWorkflowRepository? workflowRepository = null)
    {
        _orchestrator         = orchestrator;
        _loader               = loader;
        _agentLimitsConfig    = agentLimitsConfig;
        _taskAffinitiesConfig = taskAffinitiesConfig;
        _policyEngine         = policyEngine;
        _workflowRepository   = workflowRepository;
        _logger               = logger;
    }

    // ── Public API ─────────────────────────────────────────────────────────────

    public async Task<WorkflowInstance> StartAsync(StartWorkflowRequest req, CancellationToken ct)
    {
        // Resolve definition
        var def = FindDefinition(req.DefinitionId, req.WorkspacePath);
        if (def is null)
            throw new InvalidOperationException($"Workflow definition '{req.DefinitionId}' not found.");

        // Build instance
        var inst = new WorkflowInstance
        {
            DefinitionId         = def.Id,
            DefinitionName       = def.Name,
            InputContext         = req.Inputs ?? [],
            FilePaths            = req.FilePaths ?? [],
            DefaultModelId       = req.DefaultModelId,
            DefaultModelProvider = req.DefaultModelProvider,
            ModelEndpoint        = req.ModelEndpoint,
            WorkspacePath        = req.WorkspacePath,
        };

        // Apply parameter defaults for any missing inputs
        foreach (var param in def.Parameters)
        {
            if (!inst.InputContext.ContainsKey(param.Name) && param.Default is not null)
                inst.InputContext[param.Name] = param.Default;
        }

        // Initialise all step executions as Pending
        foreach (var step in def.Steps)
            inst.StepExecutions[step.Id] = new WorkflowStepExecution { StepId = step.Id };

        _active[inst.InstanceId] = (inst, def);
        _locks[inst.InstanceId]  = new SemaphoreSlim(1, 1);

        _logger.LogInformation(
            "Workflow '{Name}' started (instance {Id}, {StepCount} steps)",
            def.Name, inst.InstanceId, def.Steps.Count);

        // Persist before submitting steps so we don't lose it on crash
        await PersistInstanceAsync(inst);

        // Submit all root steps (no dependencies) — handles agent, tool, and constraint types
        await SubmitReadyStepsAsync(inst, def, ct);

        BroadcastUpdate(inst);
        return inst;
    }

    /// <summary>
    /// Recover running/paused workflow instances from the database after a service restart.
    /// Called from ServiceLifetime.StartAsync before the orchestrator starts processing.
    /// </summary>
    public async Task RecoverRunningInstancesAsync(CancellationToken ct)
    {
        if (_workflowRepository is null) return;

        var instances = await _workflowRepository.LoadRunningInstancesAsync();
        if (instances.Count == 0) return;

        _logger.LogInformation("Recovering {Count} workflow instance(s) from database", instances.Count);

        foreach (var inst in instances)
        {
            var def = FindDefinition(inst.DefinitionId, inst.WorkspacePath);
            if (def is null)
            {
                _logger.LogWarning(
                    "Cannot recover workflow instance {Id}: definition '{DefId}' not found",
                    inst.InstanceId, inst.DefinitionId);
                continue;
            }

            // Ensure all steps defined in the definition have execution records
            foreach (var step in def.Steps)
            {
                if (!inst.StepExecutions.ContainsKey(step.Id))
                    inst.StepExecutions[step.Id] = new WorkflowStepExecution { StepId = step.Id };
            }

            _active[inst.InstanceId] = (inst, def);
            _locks[inst.InstanceId]  = new SemaphoreSlim(1, 1);

            // Re-register reverse lookup for steps that have a TaskId
            foreach (var (stepId, stepExec) in inst.StepExecutions)
            {
                if (stepExec.TaskId is not null
                    && stepExec.Status is WorkflowStepStatus.Running or WorkflowStepStatus.Pending)
                {
                    _taskToStep[stepExec.TaskId] = (inst.InstanceId, stepId);
                }
            }

            // Tool steps that were Running when the service died cannot be recovered
            // (the process is gone). Mark them Failed so the DAG doesn't stall.
            foreach (var step in def.Steps.Where(s => s.Type == "tool"))
            {
                var exec = inst.StepExecutions[step.Id];
                if (exec.Status == WorkflowStepStatus.Running)
                {
                    exec.Status = WorkflowStepStatus.Failed;
                    exec.Error  = "Service restarted while tool step was running; process lost.";
                    SkipDownstream(step.Id, inst, def);
                }
            }

            // Re-submit any steps that were Pending (the orchestrator may not have their tasks).
            // Steps that were Running had their tasks recovered by AgentOrchestrator.StartProcessingAsync.
            var pendingSteps = def.Steps
                .Where(s => inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d =>
                                inst.StepExecutions.TryGetValue(d, out var e)
                                && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            foreach (var step in pendingSteps)
                await SubmitReadyStepsAsync(inst, def, ct);

            _logger.LogInformation(
                "Recovered workflow '{Name}' (instance {Id}, {Pending} step(s) re-submitted)",
                inst.DefinitionName, inst.InstanceId, pendingSteps.Count);
        }
    }

    /// <summary>
    /// Called by AgentOrchestrator whenever any task update is received.
    /// Only acts on tasks that belong to a workflow step.
    /// </summary>
    public async Task OnTaskUpdateAsync(TaskStatusResponse status)
    {
        if (!_taskToStep.TryGetValue(status.TaskId, out var stepRef))
            return;

        var (instanceId, stepId) = stepRef;
        if (!_active.TryGetValue(instanceId, out var entry))
            return;

        var (inst, def) = entry;
        var lk = _locks[instanceId];
        await lk.WaitAsync();
        try
        {
            var stepExec = inst.StepExecutions[stepId];

            // Map task status → workflow step status
            switch (status.Status)
            {
                case AgentTaskStatus.Running:
                    stepExec.Status    = WorkflowStepStatus.Running;
                    stepExec.StartedAt = status.StartedAt;
                    BroadcastUpdate(inst);
                    return; // more updates will come

                case AgentTaskStatus.Completed:
                    stepExec.Status      = WorkflowStepStatus.Completed;
                    stepExec.Output      = status.Result?.Output;
                    stepExec.IssueCount  = status.Result?.Issues?.Count ?? 0;
                    stepExec.CompletedAt = status.CompletedAt;
                    break;

                case AgentTaskStatus.Failed:
                    stepExec.Status      = WorkflowStepStatus.Failed;
                    stepExec.Error       = status.StatusMessage;
                    stepExec.CompletedAt = status.CompletedAt;
                    SkipDownstream(stepId, inst, def);
                    break;

                case AgentTaskStatus.Cancelled:
                    stepExec.Status = WorkflowStepStatus.Skipped;
                    SkipDownstream(stepId, inst, def);
                    break;

                default:
                    return;
            }

            _logger.LogDebug(
                "Workflow {InstanceId} step '{StepId}' → {Status}",
                instanceId, stepId, stepExec.Status);

            await EvaluateNextStepsAsync(inst, def, stepId);

            if (IsInstanceDone(inst, def))
            {
                inst.Status      = inst.StepExecutions.Values.Any(s => s.Status == WorkflowStepStatus.Failed)
                                     ? WorkflowStatus.Failed
                                     : WorkflowStatus.Completed;
                inst.CompletedAt = DateTime.UtcNow;
                _logger.LogInformation(
                    "Workflow '{Name}' {Status} (instance {Id})",
                    inst.DefinitionName, inst.Status, inst.InstanceId);
            }

            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        finally
        {
            lk.Release();
        }
    }

    // ── Pause / Resume / Context Update (Item 5) ───────────────────────────────

    public async Task PauseAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_active.TryGetValue(instanceId, out var entry)) return;
        var (inst, _) = entry;

        if (inst.Status != WorkflowStatus.Running) return;

        var lk = _locks[instanceId];
        await lk.WaitAsync(ct);
        try
        {
            inst.IsPaused = true;
            inst.Status   = WorkflowStatus.Paused;
            _logger.LogInformation(
                "Workflow '{Name}' paused (instance {Id}) — running tasks will complete but no new tasks submitted",
                inst.DefinitionName, instanceId);
            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

    public async Task ResumeAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_active.TryGetValue(instanceId, out var entry)) return;
        var (inst, def) = entry;

        if (!inst.IsPaused) return;

        var lk = _locks[instanceId];
        await lk.WaitAsync(ct);
        try
        {
            inst.IsPaused = false;
            inst.Status   = WorkflowStatus.Running;
            _logger.LogInformation(
                "Workflow '{Name}' resumed (instance {Id})", inst.DefinitionName, instanceId);

            // Re-evaluate: submit any steps whose dependencies are all done
            await SubmitReadyStepsAsync(inst, def, ct);

            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

    /// <summary>
    /// Update one or more workflow context variables while the workflow is running.
    /// Changed values are immediately available for {{variable}} substitution in
    /// subsequently-submitted steps.
    /// </summary>
    public async Task UpdateContextAsync(
        string instanceId,
        Dictionary<string, string> updates,
        CancellationToken ct = default)
    {
        if (!_active.TryGetValue(instanceId, out var entry)) return;
        var (inst, _) = entry;

        var lk = _locks[instanceId];
        await lk.WaitAsync(ct);
        try
        {
            foreach (var (key, value) in updates)
                inst.InputContext[key] = value;

            _logger.LogInformation(
                "Workflow {Id}: context updated — keys: {Keys}", instanceId,
                string.Join(", ", updates.Keys));

            await PersistInstanceAsync(inst);
            BroadcastUpdate(inst);
        }
        finally { lk.Release(); }
    }

    // ── Cancel (Item 2 — explicit task cancellation) ───────────────────────────

    public async Task CancelAsync(string instanceId, CancellationToken ct = default)
    {
        if (!_active.TryGetValue(instanceId, out var entry)) return;

        var (inst, _) = entry;
        inst.Status      = WorkflowStatus.Cancelled;
        inst.IsPaused    = false;
        inst.CompletedAt = DateTime.UtcNow;

        // Cancel every submitted task — both queued-but-not-started (Queued in orchestrator)
        // and actively running ones. AgentOrchestrator.CancelTaskAsync handles both states.
        var submittedSteps = inst.StepExecutions.Values
            .Where(s => s.TaskId is not null
                     && s.Status is WorkflowStepStatus.Running or WorkflowStepStatus.Pending)
            .ToList();

        _logger.LogInformation(
            "Workflow '{Name}' cancelled (instance {Id}) — cancelling {N} active task(s): {TaskIds}",
            inst.DefinitionName, instanceId, submittedSteps.Count,
            string.Join(", ", submittedSteps.Select(s => s.TaskId![..Math.Min(8, s.TaskId.Length)])));

        foreach (var stepExec in submittedSteps)
            await _orchestrator.CancelTaskAsync(stepExec.TaskId!, ct);

        // Skip all steps that haven't started yet
        foreach (var stepExec in inst.StepExecutions.Values.Where(s => s.Status == WorkflowStepStatus.Pending))
            stepExec.Status = WorkflowStepStatus.Skipped;

        await PersistInstanceAsync(inst);
        BroadcastUpdate(inst);
    }

    // ── Query API ─────────────────────────────────────────────────────────────

    public List<WorkflowDefinition> GetAvailableDefinitions(string? workspacePath = null)
    {
        var defs = _loader.GetBuiltInDefinitions();
        if (!string.IsNullOrEmpty(workspacePath))
        {
            try { defs.AddRange(_loader.LoadFromWorkspace(workspacePath)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load workspace workflows from '{Path}'", workspacePath);
            }
        }
        return defs;
    }

    public WorkflowInstance? GetInstance(string instanceId)
        => _active.TryGetValue(instanceId, out var e) ? e.Inst : null;

    public List<WorkflowInstance> GetAllInstances()
        => _active.Values.Select(e => e.Inst).ToList();

    // ── Private: DAG evaluation ────────────────────────────────────────────────

    private async Task EvaluateNextStepsAsync(
        WorkflowInstance inst, WorkflowDefinition def, string completedStepId)
    {
        var completedStep = def.Steps.FirstOrDefault(s => s.Id == completedStepId);
        if (completedStep is null) return;

        var completedExec = inst.StepExecutions[completedStepId];

        // 1. Handle feedback loop: step has a Next back-edge and found issues
        if (completedStep.Next is not null
            && completedExec.Status == WorkflowStepStatus.Completed
            && completedExec.IssueCount > 0)
        {
            var loopTargetDef = def.Steps.FirstOrDefault(s => s.Id == completedStep.Next);
            if (loopTargetDef is not null)
            {
                var loopTargetExec = inst.StepExecutions[loopTargetDef.Id];
                var agentType      = WorkflowDefinitionLoader.MapAgentName(loopTargetDef.Agent ?? loopTargetDef.Id);

                // Item 1: enforce BOTH the YAML per-step cap AND the global AgentLimits cap
                var yamlMax   = completedStep.MaxIterations;
                var globalMax = _agentLimitsConfig.GetMaxIterations(agentType);
                var effectiveMax = Math.Min(yamlMax, globalMax);

                if (loopTargetExec.Iteration < effectiveMax)
                {
                    loopTargetExec.Iteration++;
                    loopTargetExec.Status = WorkflowStepStatus.Pending;
                    loopTargetExec.Output = null;
                    loopTargetExec.TaskId = null;

                    _logger.LogInformation(
                        "Workflow {Id} feedback loop: re-running '{Target}' (iteration {N}/{Max}; global cap {Global})",
                        inst.InstanceId, loopTargetDef.Id,
                        loopTargetExec.Iteration, yamlMax, globalMax);

                    await SubmitStepAsync(loopTargetDef, inst, def, CancellationToken.None);
                    return; // wait for loop target to complete before advancing downstream
                }
                else
                {
                    // Global or YAML iteration cap hit — abort the workflow with a clear error
                    _logger.LogWarning(
                        "Workflow {Id} step '{Target}' hit max iterations (YAML: {Yaml}, Global: {Global}) — aborting",
                        inst.InstanceId, loopTargetDef.Id, yamlMax, globalMax);

                    inst.Status      = WorkflowStatus.Failed;
                    inst.CompletedAt = DateTime.UtcNow;
                    loopTargetExec.Error =
                        $"Max iterations reached ({effectiveMax}): step '{loopTargetDef.Id}' " +
                        $"exceeded the configured limit. Increase AgentLimits:{agentType}:MaxIterations " +
                        $"or the step's max_iterations to allow more iterations.";
                    loopTargetExec.Status = WorkflowStepStatus.Failed;
                    SkipDownstream(loopTargetDef.Id, inst, def);
                    return;
                }
            }
        }

        // Don't submit new steps while paused (Item 5)
        if (inst.IsPaused) return;

        await SubmitReadyStepsAsync(inst, def, CancellationToken.None);
    }

    /// <summary>
    /// Evaluate routers and constraint steps (synchronous), then submit all ready
    /// agent and tool steps (async). Loops until no more synchronous steps are ready
    /// so that chains of constraints/routers resolve in a single call.
    /// </summary>
    private async Task SubmitReadyStepsAsync(
        WorkflowInstance inst, WorkflowDefinition def, CancellationToken ct)
    {
        // ── Phase 1: drain synchronous steps (routers + constraints) ──────────
        // Loop until no more synchronous steps become ready; this handles chains
        // where a constraint's completion immediately unlocks another constraint.
        bool anySync;
        do
        {
            anySync = false;

            // Routers
            var readyRouters = def.Steps
                .Where(s => s.Type == "router"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            foreach (var router in readyRouters)
            {
                inst.StepExecutions[router.Id].Status = WorkflowStepStatus.Completed;
                var targetId = EvaluateRouter(router, inst);
                if (targetId is not null)
                {
                    var targetDef = def.Steps.FirstOrDefault(s => s.Id == targetId);
                    if (targetDef is not null && inst.StepExecutions[targetId].Status == WorkflowStepStatus.Pending)
                        await SubmitStepAsync(targetDef, inst, def, ct);
                }
                anySync = true;
            }

            // Constraints (evaluate synchronously, no I/O)
            var readyConstraints = def.Steps
                .Where(s => s.Type == "constraint"
                         && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                         && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                                 && e.Status == WorkflowStepStatus.Completed))
                .ToList();

            foreach (var constraint in readyConstraints)
            {
                ExecuteConstraintStep(constraint, inst, def);
                anySync = true;
            }
        }
        while (anySync);

        // ── Phase 2: submit async steps (tools + agents) ──────────────────────

        var readyTools = def.Steps
            .Where(s => s.Type == "tool"
                     && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                     && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                             && e.Status == WorkflowStepStatus.Completed))
            .ToList();

        foreach (var tool in readyTools)
            ExecuteToolStepInBackground(tool, inst, def);

        var readyAgents = def.Steps
            .Where(s => s.Type == "agent"
                     && inst.StepExecutions[s.Id].Status == WorkflowStepStatus.Pending
                     && s.DependsOn.All(d => inst.StepExecutions.TryGetValue(d, out var e)
                                             && e.Status == WorkflowStepStatus.Completed))
            .ToList();

        if (readyAgents.Count > 0)
            await Task.WhenAll(readyAgents.Select(s => SubmitStepAsync(s, inst, def, ct)));
    }

    private async Task SubmitStepAsync(
        WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def, CancellationToken ct)
    {
        if (inst.StepExecutions[stepDef.Id].Status == WorkflowStepStatus.Running)
            return; // already submitted

        // Item 3: Policy check before submitting
        var policyResult = _policyEngine.Check(stepDef, inst);
        if (!policyResult.IsAllowed)
        {
            var stepExec = inst.StepExecutions[stepDef.Id];
            stepExec.Status = WorkflowStepStatus.Failed;
            stepExec.Error  = $"[Policy] {policyResult.DenyReason}";
            SkipDownstream(stepDef.Id, inst, def);
            _logger.LogWarning(
                "Workflow {Id} step '{StepId}' blocked by policy: {Reason}",
                inst.InstanceId, stepDef.Id, policyResult.DenyReason);
            // Persist updated state and notify the UI so the blocked step is visible immediately.
            await PersistInstanceAsync(inst);
            OnWorkflowUpdate?.Invoke(inst);
            return;
        }

        // Resolve prompt template
        var basePrompt    = stepDef.Prompt ?? $"Process the following with a {stepDef.Agent ?? stepDef.Id} agent.";
        var resolvedPrompt = PromptTemplateEngine.Resolve(basePrompt, inst.InputContext, inst.StepExecutions);

        // Item 6: Smart Router — step override → instance default → TaskAffinities
        var agentType     = WorkflowDefinitionLoader.MapAgentName(stepDef.Agent ?? stepDef.Id);
        var modelProvider = stepDef.ModelProvider ?? inst.DefaultModelProvider;
        var modelId       = stepDef.ModelId       ?? inst.DefaultModelId;

        if (string.IsNullOrEmpty(modelProvider) || string.IsNullOrEmpty(modelId))
        {
            var (affinityProvider, affinityModel) = _taskAffinitiesConfig.GetDefaultFor(agentType);
            if (string.IsNullOrEmpty(modelProvider)) modelProvider = affinityProvider;
            if (string.IsNullOrEmpty(modelId))       modelId       = affinityModel;

            _logger.LogDebug(
                "Workflow {Id} step '{StepId}': no model specified, using affinity → {Provider}/{Model}",
                inst.InstanceId, stepDef.Id, modelProvider, modelId);
        }

        if (!Enum.TryParse<ModelProvider>(modelProvider, ignoreCase: true, out var mp))
            mp = ModelProvider.Claude;

        var task = new AgentTask
        {
            AgentType     = agentType,
            ModelProvider = mp,
            ModelId       = modelId,
            Description   = resolvedPrompt,
            FilePaths     = inst.FilePaths,
            Metadata      = new Dictionary<string, string>
            {
                ["workflowInstanceId"] = inst.InstanceId,
                ["workflowStepId"]     = stepDef.Id,
                ["workflowStepLabel"]  = stepDef.Id,
            }
        };

        if (!string.IsNullOrEmpty(inst.ModelEndpoint))
            task.Metadata["modelEndpoint"] = inst.ModelEndpoint;

        var taskId = await _orchestrator.SubmitTaskAsync(task, ct);

        // Register reverse lookup
        _taskToStep[taskId] = (inst.InstanceId, stepDef.Id);

        // Update step execution state
        var exec     = inst.StepExecutions[stepDef.Id];
        exec.TaskId    = taskId;
        exec.Status    = WorkflowStepStatus.Running;
        exec.StartedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "Workflow {InstanceId} submitted step '{StepId}' as task {TaskId} ({Agent} via {Provider}/{Model})",
            inst.InstanceId, stepDef.Id, taskId[..Math.Min(8, taskId.Length)],
            agentType, mp, modelId);
    }

    // ── Router evaluation ──────────────────────────────────────────────────────

    private string? EvaluateRouter(WorkflowStepDef routerStep, WorkflowInstance inst)
    {
        if (routerStep.Router is null) return null;

        var depExecs = routerStep.DependsOn
            .Select(id => inst.StepExecutions.GetValueOrDefault(id))
            .Where(e => e is not null)
            .Cast<WorkflowStepExecution>()
            .ToList();

        // Use dep with the highest issue count (most relevant for routing decisions)
        var primaryDep = depExecs.OrderByDescending(e => e.IssueCount).FirstOrDefault()
                      ?? depExecs.FirstOrDefault();

        if (primaryDep is null) return null;

        foreach (var branch in routerStep.Router.Branches)
        {
            if (EvaluateCondition(branch.Condition, primaryDep))
            {
                _logger.LogDebug(
                    "Router '{RouterId}' matched condition '{Cond}' → '{Target}'",
                    routerStep.Id, branch.Condition, branch.Target);
                return branch.Target;
            }
        }

        _logger.LogWarning("Router '{RouterId}' had no matching condition — skipping downstream", routerStep.Id);
        return null;
    }

    private static bool EvaluateCondition(string condition, WorkflowStepExecution dep)
    {
        var c = condition.Trim().ToLowerInvariant();
        return c switch
        {
            "hasissues" or "has_issues"    => dep.IssueCount > 0,
            "success"   or "approved"      => dep.Status == WorkflowStepStatus.Completed && dep.IssueCount == 0,
            "failed"    or "error"         => dep.Status == WorkflowStepStatus.Failed,
            _ when c.StartsWith("output.contains(") => EvaluateContains(c, dep.Output ?? ""),
            _ => false,
        };
    }

    private static bool EvaluateContains(string condition, string output)
    {
        var start = condition.IndexOf('(') + 1;
        var end   = condition.LastIndexOf(')');
        if (start >= end) return false;
        var arg = condition[start..end].Trim('\'', '"', ' ');
        return output.Contains(arg, StringComparison.OrdinalIgnoreCase);
    }

    // ── Constraint step execution ──────────────────────────────────────────────

    private void ExecuteConstraintStep(WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        exec.Status    = WorkflowStepStatus.Running;
        exec.StartedAt = DateTime.UtcNow;

        var (passed, reason) = EvaluateConstraintExpr(stepDef.ConstraintExpr ?? "", inst);
        exec.Output      = reason;
        exec.CompletedAt = DateTime.UtcNow;

        if (passed)
        {
            exec.Status = WorkflowStepStatus.Completed;
            _logger.LogInformation(
                "Workflow {Id} constraint '{StepId}' passed: {Reason}",
                inst.InstanceId, stepDef.Id, reason);
        }
        else if (stepDef.OnConstraintFail.Equals("warn", StringComparison.OrdinalIgnoreCase))
        {
            exec.Status     = WorkflowStepStatus.Completed;
            exec.IssueCount = 1;
            _logger.LogWarning(
                "Workflow {Id} constraint '{StepId}' failed (warn): {Reason}",
                inst.InstanceId, stepDef.Id, reason);
        }
        else
        {
            exec.Status = WorkflowStepStatus.Failed;
            exec.Error  = $"Constraint failed: {reason}";
            SkipDownstream(stepDef.Id, inst, def);
            _logger.LogWarning(
                "Workflow {Id} constraint '{StepId}' failed: {Reason}",
                inst.InstanceId, stepDef.Id, reason);
        }
    }

    private (bool Passed, string Reason) EvaluateConstraintExpr(string expr, WorkflowInstance inst)
    {
        expr = expr.Trim();

        // exit_code(step_id) == N
        var m = Regex.Match(expr, @"exit_code\((\w+)\)\s*==\s*(\d+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId   = m.Groups[1].Value;
            var expected = int.Parse(m.Groups[2].Value);
            if (inst.StepExecutions.TryGetValue(stepId, out var e) && e.ExitCode.HasValue)
                return (e.ExitCode.Value == expected,
                    $"exit_code({stepId}) = {e.ExitCode.Value} (expected {expected})");
            return (false, $"Step '{stepId}' has no exit code recorded.");
        }

        // output(step_id).contains('text')
        m = Regex.Match(expr, @"output\((\w+)\)\.contains\('(.+?)'\)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId = m.Groups[1].Value;
            var text   = m.Groups[2].Value;
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
            {
                var contains = (e.Output ?? "").Contains(text, StringComparison.OrdinalIgnoreCase);
                return (contains, $"output({stepId}).contains('{text}') = {contains}");
            }
            return (false, $"Step '{stepId}' not found.");
        }

        // issue_count(step_id) == N
        m = Regex.Match(expr, @"issue_count\((\w+)\)\s*==\s*(\d+)", RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var stepId   = m.Groups[1].Value;
            var expected = int.Parse(m.Groups[2].Value);
            if (inst.StepExecutions.TryGetValue(stepId, out var e))
                return (e.IssueCount == expected,
                    $"issue_count({stepId}) = {e.IssueCount} (expected {expected})");
            return (false, $"Step '{stepId}' not found.");
        }

        return (false, $"Unrecognised constraint expression: '{expr}'");
    }

    // ── Tool step execution ────────────────────────────────────────────────────

    /// <summary>
    /// Marks the step Running immediately (under whatever lock the caller holds),
    /// then fires a background task that runs the process and re-acquires the lock
    /// to update state and advance the DAG when done.
    /// </summary>
    private void ExecuteToolStepInBackground(WorkflowStepDef stepDef, WorkflowInstance inst, WorkflowDefinition def)
    {
        var exec = inst.StepExecutions[stepDef.Id];
        exec.Status    = WorkflowStepStatus.Running;
        exec.StartedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "Workflow {InstanceId} starting tool step '{StepId}': {Command}",
            inst.InstanceId, stepDef.Id, stepDef.Command);

        BroadcastUpdate(inst);

        // Capture state needed by the background task
        var instanceId = inst.InstanceId;
        var command    = PromptTemplateEngine.Resolve(
            stepDef.Command ?? "", inst.InputContext, inst.StepExecutions);
        var workingDir = string.IsNullOrWhiteSpace(stepDef.WorkingDir)
            ? inst.WorkspacePath ?? Directory.GetCurrentDirectory()
            : PromptTemplateEngine.Resolve(stepDef.WorkingDir, inst.InputContext, inst.StepExecutions);
        var policy     = stepDef.ExitCodePolicy.ToUpperInvariant();

        _ = Task.Run(async () =>
        {
            string output;
            int    exitCode;
            try
            {
                (output, exitCode) = await RunProcessAsync(command, workingDir);
            }
            catch (Exception ex)
            {
                output   = ex.Message;
                exitCode = -1;
                _logger.LogError(ex,
                    "Workflow {Id} tool step '{StepId}' threw an exception", instanceId, stepDef.Id);
            }

            if (!_active.TryGetValue(instanceId, out var entry)) return;
            var (liveInst, liveDef) = entry;
            var lk = _locks[instanceId];
            await lk.WaitAsync();
            try
            {
                var liveExec         = liveInst.StepExecutions[stepDef.Id];
                liveExec.ExitCode    = exitCode;
                liveExec.Output      = output;
                liveExec.CompletedAt = DateTime.UtcNow;

                if (exitCode == 0 || policy is "IGNORE")
                {
                    liveExec.Status = WorkflowStepStatus.Completed;
                }
                else if (policy == "WARN_ON_NONZERO")
                {
                    liveExec.Status     = WorkflowStepStatus.Completed;
                    liveExec.IssueCount = 1;
                    _logger.LogWarning(
                        "Workflow {Id} tool '{StepId}' exited {Code} (WARN_ON_NONZERO)",
                        instanceId, stepDef.Id, exitCode);
                }
                else // FAIL_ON_NONZERO
                {
                    liveExec.Status = WorkflowStepStatus.Failed;
                    liveExec.Error  = $"Command exited with code {exitCode}.";
                    SkipDownstream(stepDef.Id, liveInst, liveDef);
                    _logger.LogWarning(
                        "Workflow {Id} tool '{StepId}' failed (exit code {Code})",
                        instanceId, stepDef.Id, exitCode);
                }

                await EvaluateNextStepsAsync(liveInst, liveDef, stepDef.Id);

                if (IsInstanceDone(liveInst, liveDef))
                {
                    liveInst.Status = liveInst.StepExecutions.Values.Any(
                        s => s.Status == WorkflowStepStatus.Failed)
                        ? WorkflowStatus.Failed
                        : WorkflowStatus.Completed;
                    liveInst.CompletedAt = DateTime.UtcNow;
                    _logger.LogInformation(
                        "Workflow '{Name}' {Status} (instance {Id})",
                        liveInst.DefinitionName, liveInst.Status, instanceId);
                }

                await PersistInstanceAsync(liveInst);
                BroadcastUpdate(liveInst);
            }
            finally { lk.Release(); }
        });
    }

    private static async Task<(string Output, int ExitCode)> RunProcessAsync(
        string command, string workingDir)
    {
        // Split "dotnet build --nologo" into executable + arguments
        var parts = command.Trim().Split(' ', 2);
        var exe   = parts[0];
        var args  = parts.Length > 1 ? parts[1] : string.Empty;

        var psi = new ProcessStartInfo
        {
            FileName               = exe,
            Arguments              = args,
            WorkingDirectory       = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        var combined = string.IsNullOrWhiteSpace(stderr)
            ? stdout
            : stdout + "\n--- stderr ---\n" + stderr;

        return (combined.Trim(), proc.ExitCode);
    }

    // ── Helper methods ─────────────────────────────────────────────────────────

    private static void SkipDownstream(string failedStepId, WorkflowInstance inst, WorkflowDefinition def)
    {
        var queue   = new Queue<string>([failedStepId]);
        var visited = new HashSet<string>();
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!visited.Add(id)) continue;
            foreach (var step in def.Steps.Where(s => s.DependsOn.Contains(id)))
            {
                if (inst.StepExecutions.TryGetValue(step.Id, out var e)
                    && e.Status == WorkflowStepStatus.Pending)
                {
                    e.Status = WorkflowStepStatus.Skipped;
                    queue.Enqueue(step.Id);
                }
            }
        }
    }

    private static bool IsInstanceDone(WorkflowInstance inst, WorkflowDefinition def)
        => def.Steps
              .Where(s => s.Type is not "router")   // routers are structural, not tracked as done
              .All(s => inst.StepExecutions.TryGetValue(s.Id, out var e)
                        && e.Status is WorkflowStepStatus.Completed
                                    or WorkflowStepStatus.Failed
                                    or WorkflowStepStatus.Skipped);

    private WorkflowDefinition? FindDefinition(string id, string? workspacePath)
    {
        var all = GetAvailableDefinitions(workspacePath);
        return all.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task PersistInstanceAsync(WorkflowInstance inst)
    {
        if (_workflowRepository is null) return;
        try
        {
            if (inst.Status is WorkflowStatus.Completed or WorkflowStatus.Failed or WorkflowStatus.Cancelled)
            {
                // Keep completed instances for a brief window, then let cleanup remove them
                await _workflowRepository.SaveWorkflowInstanceAsync(inst);
                // Note: we intentionally don't delete immediately — the UI may still query them.
            }
            else
            {
                await _workflowRepository.SaveWorkflowInstanceAsync(inst);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist workflow instance {Id}", inst.InstanceId);
        }
    }

    private void BroadcastUpdate(WorkflowInstance inst) => OnWorkflowUpdate?.Invoke(inst);
}
