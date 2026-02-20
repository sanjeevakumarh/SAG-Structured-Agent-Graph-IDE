namespace SAGIDE.Core.DTOs;

public class StreamingOutputMessage
{
    public string TaskId { get; set; } = string.Empty;
    public string TextChunk { get; set; } = string.Empty;
    public int? ProgressPercent { get; set; }
    public int TokensGeneratedSoFar { get; set; }
    public bool IsLastChunk { get; set; }
    public string? Error { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
