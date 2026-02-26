using System.Text.RegularExpressions;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Resolves {{}} template variables in workflow step prompts.
/// Supports:
///   {{param_name}}         — value from user-supplied InputContext
///   {{step_id.output}}     — raw LLM output from a prior step (truncated to MaxOutputChars)
/// MaxOutputChars is configurable via SAGIDE:Orchestration:MaxStepOutputChars (default 4000).
/// Set once at startup via <see cref="Configure"/>.
/// </summary>
public static partial class PromptTemplateEngine
{
    /// <summary>
    /// Maximum characters of step output included in {{step_id.output}} substitutions.
    /// Set once at application startup from IConfiguration; thread-safe for reads after init.
    /// </summary>
    public static int MaxOutputChars { get; set; } = 4000;

    /// <summary>Applies configuration values. Call once from Program.cs after building config.</summary>
    public static void Configure(int maxOutputChars) => MaxOutputChars = maxOutputChars;

    public static string Resolve(
        string template,
        Dictionary<string, string> inputContext,
        Dictionary<string, WorkflowStepExecution> stepExecutions)
    {
        return TemplateVarRegex().Replace(template, match =>
        {
            var key = match.Groups[1].Value;

            // {{step_id.field}} — currently only .output is supported
            if (key.Contains('.'))
            {
                var dotIdx = key.IndexOf('.');
                var stepId = key[..dotIdx];
                var field  = key[(dotIdx + 1)..];

                if (stepExecutions.TryGetValue(stepId, out var stepExec))
                {
                    return field switch
                    {
                        "output" => stepExec.Output is { } o
                            ? (o.Length > MaxOutputChars
                                ? string.Concat(o.AsSpan(0, MaxOutputChars), "\n...[truncated]")
                                : o)
                            : string.Empty,
                        "exit_code"   => stepExec.ExitCode?.ToString() ?? "[no exit code]",
                        "issue_count" => stepExec.IssueCount.ToString(),
                        "status"      => stepExec.Status.ToString().ToLowerInvariant(),
                        _             => $"[{key}: unknown field]",
                    };
                }

                return $"[{key}: not available]";
            }

            // {{param_name}}
            return inputContext.TryGetValue(key, out var val) ? val : $"[{key}: not set]";
        });
    }

    // [\w-]+ allows hyphens in step IDs (e.g. {{generate-code.output}}) and parameter names.
    [GeneratedRegex(@"\{\{([\w-]+(?:\.[\w-]+)?)\}\}")]
    private static partial Regex TemplateVarRegex();
}
