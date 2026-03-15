namespace SAGIDE.Contracts;

/// <summary>
/// Read-only access to registered skill definitions.
/// Implemented by the core service; consumed by orchestration, endpoints, and external applications.
/// </summary>
public interface ISkillRegistry
{
    IReadOnlyList<SkillDefinition> GetAll();
    IReadOnlyList<SkillDefinition> GetByDomain(string domain);
    SkillDefinition? GetByKey(string domain, string name);

    /// <summary>
    /// Resolves a skill reference that is either "domain/name" or just "name".
    /// When only a name is given, searches all domains and returns the first match.
    /// </summary>
    SkillDefinition? Resolve(string skillRef);

    /// <summary>
    /// Shared text blocks (e.g. from prompt-blocks.yaml).
    /// Available in skill templates as <c>{{blocks.block_name}}</c>.
    /// </summary>
    IReadOnlyDictionary<string, string> PromptBlocks { get; }
}

/// <summary>
/// Write API for registering/unregistering skills at runtime.
/// External applications call these methods (via REST) to push their skill definitions
/// into the global registry without requiring filesystem access.
/// </summary>
public interface ISkillRegistrationService
{
    void Register(SkillDefinition skill);
    void RegisterBulk(IEnumerable<SkillDefinition> skills);
    bool Unregister(string domain, string name);
}
