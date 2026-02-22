using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

public interface IAgentProvider
{
    ModelProvider Provider { get; }
    int LastInputTokens { get; }
    int LastOutputTokens { get; }

    /// <summary>Complete a prompt and return the full response (non-streaming fallback).</summary>
    Task<string> CompleteAsync(string prompt, ModelConfig model, CancellationToken cancellationToken = default);

    /// <summary>Stream incremental text chunks as they arrive from the provider.</summary>
    IAsyncEnumerable<string> CompleteStreamingAsync(
        string prompt, ModelConfig model, CancellationToken cancellationToken = default);

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
