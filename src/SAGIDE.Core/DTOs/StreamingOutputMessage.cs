namespace SAGIDE.Core.DTOs;

public class StreamingOutputMessage
{
    public string TaskId { get; set; } = string.Empty;
    /// <summary>Incremental text chunk from the provider</summary>
    public string TextChunk { get; set; } = string.Empty;
    /// <summary>Rough progress 0-100; only set occasionally, not on every chunk</summary>
    public int? ProgressPercent { get; set; }
    public int TokensGeneratedSoFar { get; set; }
    /// <summary>True on the final chunk — stream has ended</summary>
    public bool IsLastChunk { get; set; }
    public string? Error { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
}
