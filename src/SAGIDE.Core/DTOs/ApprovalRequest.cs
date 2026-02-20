namespace SAGIDE.Core.DTOs;

public class ApprovalRequest
{
    public string TaskId { get; set; } = string.Empty;
    public bool Approved { get; set; }
    public string? Reason { get; set; }
}
