namespace SAGIDE.Service.Infrastructure;

/// <summary>
/// Controls what task data is included in structured log output.
/// Bind from configuration section "SAGIDE:Logging".
/// </summary>
public class LoggingConfig
{
    /// <summary>
    /// When true (default), task descriptions and caller-supplied metadata values are
    /// omitted from log messages. Only a safe allowlist of metadata keys is included.
    /// Set to false to log full task context — useful for debugging but may expose
    /// prompt text or variable values in log files.
    /// </summary>
    public bool RedactSensitiveData { get; set; } = true;

    // Keys that are always safe to include in log output regardless of redaction setting.
    private static readonly HashSet<string> _allowedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "agentType", "modelProvider", "modelId", "sourceTag", "priority",
        "latencyMs", "issueCount", "changeCount", "cacheHit",
        "workflowInstanceId", "stepId", "retriedFromDlq", "originalTaskId",
        "prompt_domain", "prompt_name", "triggered_by",
    };

    /// <summary>
    /// Returns a log-safe view of the metadata dictionary.
    /// When <see cref="RedactSensitiveData"/> is true, only allowlisted keys are returned.
    /// </summary>
    public Dictionary<string, string> SanitizeMetadata(Dictionary<string, string> metadata)
    {
        if (!RedactSensitiveData)
            return metadata;

        return metadata
            .Where(kv => _allowedKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    /// Returns a log-safe representation of the task description.
    /// When <see cref="RedactSensitiveData"/> is true, only the first 120 characters are included.
    /// </summary>
    public string SanitizeDescription(string description)
    {
        if (!RedactSensitiveData || description.Length <= 120)
            return description;

        return string.Concat(description.AsSpan(0, 120), "…");
    }
}
