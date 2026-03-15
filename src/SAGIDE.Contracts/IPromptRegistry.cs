namespace SAGIDE.Contracts;

/// <summary>
/// Read-only access to registered prompt definitions.
/// Implemented by the core service; consumed by orchestration, endpoints, and external applications.
/// </summary>
public interface IPromptRegistry
{
    IReadOnlyList<PromptDefinition> GetAll();
    IReadOnlyList<PromptDefinition> GetByDomain(string domain);
    PromptDefinition? GetByKey(string domain, string name);
    IReadOnlyList<PromptDefinition> GetScheduled();
}

/// <summary>
/// Write API for registering/unregistering prompts at runtime.
/// External applications call these methods (via REST) to push their prompt definitions
/// into the global registry without requiring filesystem access.
/// </summary>
public interface IPromptRegistrationService
{
    void Register(PromptDefinition prompt);
    void RegisterBulk(IEnumerable<PromptDefinition> prompts);
    bool Unregister(string domain, string name);
}
