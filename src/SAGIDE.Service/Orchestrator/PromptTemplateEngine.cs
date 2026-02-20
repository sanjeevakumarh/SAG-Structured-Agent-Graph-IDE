using System.Text.RegularExpressions;
using SAGIDE.Core.Models;

namespace SAGIDE.Service.Orchestrator;

/// <summary>
/// Resolves {{}} template variables in workflow step prompts.
/// Supports:
///   {{param_name}}         — value from user-supplied InputContext
///   {{step_id.output}}     — raw LLM output from a prior step (truncated to 4000 chars)
/// </summary>
public static partial class PromptTemplateEngine
{
    private const int MaxOutputChars = 4000;

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
