using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

public interface IAgentProvider
{
    ModelProvider Provider { get; }
    int LastInputTokens { get; }
    int LastOutputTokens { get; }

    Task<string> CompleteAsync(string prompt, ModelConfig model, CancellationToken cancellationToken = default);

    IAsyncEnumerable<string> CompleteStreamingAsync(
        string prompt, ModelConfig model, CancellationToken cancellationToken = default);

    Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default);
}
