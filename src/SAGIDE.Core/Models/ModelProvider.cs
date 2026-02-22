namespace SAGIDE.Core.Models;

public enum ModelProvider { Claude, Codex, Gemini, Ollama }

public record ModelConfig(ModelProvider Provider, string ModelId, string? ApiKey = null, string? Endpoint = null)
{
    // Default model IDs match SAGIDE:TaskAffinities in appsettings.json
    public static ModelConfig Claude(string modelId = "claude-sonnet-4-6", string? apiKey = null)
        => new(ModelProvider.Claude, modelId, apiKey);

    public static ModelConfig Codex(string modelId = "gpt-4o", string? apiKey = null)
        => new(ModelProvider.Codex, modelId, apiKey);

    public static ModelConfig Gemini(string modelId = "gemini-2.0-flash", string? apiKey = null)
        => new(ModelProvider.Gemini, modelId, apiKey);

    // Generic Ollama factory — endpoint and model come from SAGIDE:Ollama config in appsettings.json
    public static ModelConfig Local(string modelId, string endpoint = "http://localhost:11434")
        => new(ModelProvider.Ollama, modelId, Endpoint: endpoint);
}
