using Microsoft.Extensions.Logging;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Loads WorkflowDefinition objects from:
///   1. Built-in YAML templates (hardcoded strings, always available)
///   2. Workspace .agentide/workflows/*.yaml files (workspace-specific)
/// </summary>
public class WorkflowDefinitionLoader
{
    private readonly ILogger<WorkflowDefinitionLoader> _logger;
    private readonly IDeserializer _deserializer;

    public WorkflowDefinitionLoader(ILogger<WorkflowDefinitionLoader> logger)
    {
        _logger = logger;
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(UnderscoredNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>Maps YAML agent names to AgentType enum values.</summary>
    public static AgentType MapAgentName(string name) => name.ToLowerInvariant() switch
    {
        "coder" or "codegenerator" or "generator" => AgentType.Refactoring,
        "reviewer" or "codereviewer" or "codereview" => AgentType.CodeReview,
        "tester" or "testgeneration" or "unittester" => AgentType.TestGeneration,
        "security" or "securityreview" or "securityreviewer" => AgentType.SecurityReview,
        "documenter" or "documentation" or "documentor" => AgentType.Documentation,
        "debug" or "debugger" => AgentType.Debug,
        "refactoring" or "refactor" => AgentType.Refactoring,
        _ => AgentType.CodeReview
    };

    /// <summary>Loads all built-in workflow definitions.</summary>
    public List<WorkflowDefinition> GetBuiltInDefinitions()
    {
        var templates = new List<(string id, string yaml)>
        {
            ("ship-feature",     BuiltInYaml.ShipFeature),
            ("review-fix-loop",  BuiltInYaml.ReviewFixLoop),
            ("code-audit",       BuiltInYaml.CodeAudit),
            ("tdd-workflow",     BuiltInYaml.TddWorkflow),
            ("write-and-test",   BuiltInYaml.WriteAndTest),
            ("build-and-verify", BuiltInYaml.BuildAndVerify),
        };

        var result = new List<WorkflowDefinition>();
        foreach (var (id, yaml) in templates)
        {
            try
            {
                var def = ParseYaml(yaml, id);
                def.IsBuiltIn = true;
                result.Add(def);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to parse built-in workflow template '{Id}'", id);
            }
        }
        return result;
    }

    /// <summary>Scans workspacePath/.agentide/workflows/*.yaml and parses each file.</summary>
    public List<WorkflowDefinition> LoadFromWorkspace(string workspacePath)
    {
        var result = new List<WorkflowDefinition>();
        var dir = Path.Combine(workspacePath, ".agentide", "workflows");
        if (!Directory.Exists(dir))
            return result;

        foreach (var file in Directory.EnumerateFiles(dir, "*.yaml")
                    .Concat(Directory.EnumerateFiles(dir, "*.yml")))
        {
            try
            {
                var yaml = File.ReadAllText(file);
                var id   = Path.GetFileNameWithoutExtension(file);
                var def  = ParseYaml(yaml, id);
                def.IsBuiltIn = false;
                result.Add(def);
                _logger.LogDebug("Loaded workflow '{Name}' from {File}", def.Name, file);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to parse workflow file '{File}'", file);
            }
        }
        return result;
    }

    /// <summary>Parses a YAML string into a WorkflowDefinition.</summary>
    public WorkflowDefinition ParseYaml(string yaml, string fallbackId)
    {
        var raw = _deserializer.Deserialize<YamlWorkflowDefinition>(yaml);

        var def = new WorkflowDefinition
        {
            Id          = raw.Id ?? fallbackId,
            Name        = raw.Name ?? fallbackId,
            Description = raw.Description ?? string.Empty,
        };

        if (raw.Parameters is not null)
        {
            foreach (var p in raw.Parameters)
            {
                def.Parameters.Add(new WorkflowParameter
                {
                    Name    = p.Name ?? string.Empty,
                    Type    = p.Type ?? "string",
                    Default = p.Default,
                });
            }
        }

        if (raw.Steps is not null)
        {
            foreach (var s in raw.Steps)
            {
                var step = new WorkflowStepDef
                {
                    Id                = s.Id ?? string.Empty,
                    Type              = s.Type ?? "agent",
                    Agent             = s.Agent,
                    DependsOn         = s.DependsOn ?? [],
                    Prompt            = s.Prompt,
                    ModelId           = s.ModelId,
                    ModelProvider     = s.ModelProvider,
                    Next              = s.Next,
                    MaxIterations     = s.MaxIterations > 0 ? s.MaxIterations : 1,
                    Command           = s.Command,
                    WorkingDir        = s.WorkingDir,
                    ExitCodePolicy    = s.ExitCodePolicy ?? "FAIL_ON_NONZERO",
                    ConstraintExpr    = s.ConstraintExpr,
                    OnConstraintFail  = s.OnConstraintFail ?? "fail",
                };

                if (s.Branches is { Count: > 0 })
                {
                    step.Router = new RouterConfig
                    {
                        Branches = s.Branches.Select(b => new RouterBranch
                        {
                            Condition = b.Condition ?? string.Empty,
                            Target    = b.Target ?? string.Empty,
                        }).ToList()
                    };
                }

                def.Steps.Add(step);
            }
        }

        var validationErrors = ValidateWorkflow(def);
        if (validationErrors.Count > 0)
            throw new InvalidOperationException(
                $"Workflow '{def.Name}' has {validationErrors.Count} validation error(s):\n  - " +
                string.Join("\n  - ", validationErrors));

        return def;
    }

    /// <summary>
    /// Validates a parsed WorkflowDefinition for:
    ///   1. Unknown step IDs referenced in depends_on, next:, and router branch targets
    ///   2. Cycles in the depends_on graph (next: back-edges are intentional and excluded)
    /// Returns a list of human-readable error strings (empty list = valid).
    /// </summary>
    private static List<string> ValidateWorkflow(WorkflowDefinition def)
    {
        var errors = new List<string>();
        var stepIds = def.Steps.Select(s => s.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // ── 1. Reference validation ────────────────────────────────────────────
        foreach (var step in def.Steps)
        {
            foreach (var dep in step.DependsOn)
                if (!stepIds.Contains(dep))
                    errors.Add($"Step '{step.Id}': depends_on references unknown step '{dep}'.");

            if (step.Next is not null && !stepIds.Contains(step.Next))
                errors.Add($"Step '{step.Id}': next: references unknown step '{step.Next}'.");

            if (step.Router is not null)
                foreach (var branch in step.Router.Branches)
                    if (!string.IsNullOrEmpty(branch.Target) && !stepIds.Contains(branch.Target))
                        errors.Add(
                            $"Step '{step.Id}': router branch (condition '{branch.Condition}') " +
                            $"targets unknown step '{branch.Target}'.");

            if (step.Type == "tool" && string.IsNullOrWhiteSpace(step.Command))
                errors.Add($"Step '{step.Id}' (type: tool) must have a 'command' field.");

            if (step.Type == "constraint" && string.IsNullOrWhiteSpace(step.ConstraintExpr))
                errors.Add($"Step '{step.Id}' (type: constraint) must have a 'constraint_expr' field.");
        }

        // ── 2. Cycle detection in depends_on DAG ──────────────────────────────
        // 3-color DFS: 0=unvisited, 1=in-stack (gray = currently being processed), 2=done (black)
        // Note: next: back-edges are intentional feedback loops and are NOT checked here.
        var color = def.Steps.ToDictionary(
            s => s.Id,
            _ => 0,
            StringComparer.OrdinalIgnoreCase);

        void Dfs(string id)
        {
            if (!color.ContainsKey(id)) return;
            color[id] = 1;

            var stepDeps = def.Steps
                .FirstOrDefault(s => s.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
                ?.DependsOn ?? [];

            foreach (var dep in stepDeps)
            {
                if (!color.TryGetValue(dep, out var depColor)) continue;
                if (depColor == 1)
                    errors.Add(
                        $"Circular dependency: step '{dep}' ← step '{id}' creates a cycle in depends_on. " +
                        "Tip: use next: for intentional feedback loops, not depends_on.");
                else if (depColor == 0)
                    Dfs(dep);
            }
            color[id] = 2;
        }

        foreach (var step in def.Steps)
            if (color.TryGetValue(step.Id, out var c) && c == 0)
                Dfs(step.Id);

        return errors;
    }

    // ── YAML raw deserialization POCOs (snake_case fields via YamlDotNet) ──────

    private class YamlWorkflowDefinition
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Description { get; set; }
        public List<YamlParameter>? Parameters { get; set; }
        public List<YamlStep>? Steps { get; set; }
    }

    private class YamlParameter
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public string? Default { get; set; }
    }

    private class YamlStep
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Agent { get; set; }
        public List<string>? DependsOn { get; set; }
        public string? Prompt { get; set; }
        public string? ModelId { get; set; }
        public string? ModelProvider { get; set; }
        public string? Next { get; set; }
        public int MaxIterations { get; set; } = 1;
        public List<YamlBranch>? Branches { get; set; }
        // Tool step fields
        public string? Command { get; set; }
        public string? WorkingDir { get; set; }
        public string? ExitCodePolicy { get; set; }
        // Constraint step fields
        public string? ConstraintExpr { get; set; }
        public string? OnConstraintFail { get; set; }
    }

    private class YamlBranch
    {
        public string? Condition { get; set; }
        public string? Target { get; set; }
    }
}

// ── Built-in YAML templates ───────────────────────────────────────────────────

internal static class BuiltInYaml
{
    public const string ShipFeature = """
        name: "Ship Feature"
        description: "Generate code, review and test in parallel, security scan, then document."
        parameters:
          - name: feature_description
            type: string
            default: "the requested feature"
        steps:
          - id: generate_code
            agent: Coder
            prompt: "Implement the following feature: {{feature_description}}"
          - id: code_review
            agent: Reviewer
            depends_on: [generate_code]
            prompt: "Review this code for quality and correctness:\n\n{{generate_code.output}}"
          - id: unit_tests
            agent: Tester
            depends_on: [generate_code]
            prompt: "Generate comprehensive unit tests for this code:\n\n{{generate_code.output}}"
          - id: security_scan
            agent: Security
            depends_on: [code_review, unit_tests]
            prompt: "Security scan this code:\n\n{{generate_code.output}}\n\nReview findings:\n{{code_review.output}}"
          - id: documentation
            agent: Documenter
            depends_on: [security_scan]
            prompt: "Write documentation for this code:\n\n{{generate_code.output}}"
        """;

    public const string ReviewFixLoop = """
        name: "Review & Fix Loop"
        description: "Review code, refactor based on feedback, re-review (up to 2 rounds)."
        steps:
          - id: review
            agent: Reviewer
            prompt: "Review this code thoroughly. Return structured JSON with an 'issues' array."
          - id: fix
            agent: Coder
            depends_on: [review]
            prompt: "Fix all issues found in this review:\n\n{{review.output}}\n\nApply the fixes to the code."
            next: review
            max_iterations: 2
        """;

    public const string CodeAudit = """
        name: "Code Audit"
        description: "Run code review, debug analysis, and documentation in parallel."
        steps:
          - id: review
            agent: Reviewer
            prompt: "Perform a thorough code review."
          - id: debug
            agent: Debugger
            prompt: "Analyze for bugs, memory leaks, and race conditions."
          - id: docs
            agent: Documenter
            prompt: "Generate or update documentation for this code."
        """;

    public const string TddWorkflow = """
        name: "TDD Workflow"
        description: "Write failing tests first, then implement code to make them pass."
        steps:
          - id: write_tests
            agent: Tester
            prompt: "Write failing unit tests that specify the expected behavior. Do NOT implement the code yet."
          - id: implement
            agent: Coder
            depends_on: [write_tests]
            prompt: "Implement code to make these tests pass:\n\n{{write_tests.output}}"
          - id: check_router
            type: router
            depends_on: [implement]
            branches:
              - condition: "hasIssues"
                target: implement
              - condition: "success"
                target: refactor
          - id: refactor
            agent: Coder
            depends_on: [implement]
            prompt: "Refactor this code for clarity and maintainability:\n\n{{implement.output}}"
        """;

    public const string WriteAndTest = """
        name: "Write & Test"
        description: "Refactor code then generate comprehensive tests."
        steps:
          - id: refactor
            agent: Coder
            prompt: "Refactor and improve this code."
          - id: tests
            agent: Tester
            depends_on: [refactor]
            prompt: "Generate comprehensive tests for:\n\n{{refactor.output}}"
        """;

    public const string BuildAndVerify = """
        name: "Build & Verify"
        description: "Generate code, build it, check the build passed, then review and document."
        parameters:
          - name: feature_description
            type: string
            default: "the requested feature"
          - name: build_command
            type: string
            default: "dotnet build"
          - name: workspace_path
            type: string
            default: "."
        steps:
          - id: generate_code
            agent: Coder
            prompt: |
              Implement the following feature: {{feature_description}}
              Return complete, production-ready code with no placeholders.

          - id: build
            type: tool
            depends_on: [generate_code]
            command: "{{build_command}}"
            working_dir: "{{workspace_path}}"
            exit_code_policy: FAIL_ON_NONZERO

          - id: check_build
            type: constraint
            depends_on: [build]
            constraint_expr: "exit_code(build) == 0"
            on_constraint_fail: fail

          - id: review
            agent: Reviewer
            depends_on: [check_build]
            prompt: |
              Review this generated code for correctness, performance, and best practices.
              Build output: {{build.output}}
              Code: {{generate_code.output}}

          - id: documentation
            agent: Documenter
            depends_on: [review]
            prompt: |
              Write documentation for this feature.
              Code: {{generate_code.output}}
              Review notes: {{review.output}}
        """;
}
