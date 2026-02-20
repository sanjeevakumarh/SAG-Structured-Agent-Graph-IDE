using SAGIDE.Core.Models;

namespace SAGIDE.Core.Interfaces;

public interface IAgent
{
    AgentType AgentType { get; }
    Task<AgentResult> ExecuteAsync(AgentTask task, CancellationToken cancellationToken = default);
}
