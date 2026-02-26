namespace SAGIDE.Core.Models;

/// <summary>
/// Canonical serialization and parsing helpers for <see cref="AgentTaskStatus"/>.
/// Use these instead of <c>.ToString()</c> / <c>Enum.Parse</c> so that all
/// serialization and parsing goes through one place.
/// </summary>
public static class AgentTaskStatusExtensions
{
    /// <summary>Returns the canonical display/storage string for the status.</summary>
    public static string ToDisplayString(this AgentTaskStatus status) => status switch
    {
        AgentTaskStatus.Queued          => "Queued",
        AgentTaskStatus.Running         => "Running",
        AgentTaskStatus.WaitingApproval => "WaitingApproval",
        AgentTaskStatus.Completed       => "Completed",
        AgentTaskStatus.Failed          => "Failed",
        AgentTaskStatus.Cancelled       => "Cancelled",
        _                               => status.ToString()
    };

    /// <summary>
    /// Tries to parse <paramref name="value"/> into an <see cref="AgentTaskStatus"/>.
    /// Returns <see langword="false"/> if the value is unrecognized.
    /// </summary>
    public static bool TryParseStatus(string? value, out AgentTaskStatus status)
    {
        status = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return Enum.TryParse(value, ignoreCase: true, out status);
    }
}
