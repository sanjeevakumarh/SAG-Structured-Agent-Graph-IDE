namespace SAGIDE.Core.Models;

public enum ModelProvider { Claude, Codex, Gemini, Ollama }

public record ModelConfig(ModelProvider Provider, string ModelId, string? ApiKey = null, string? Endpoint = null)
{
    public static ModelConfig Claude(string modelId = "claude-sonnet-4-5-20250929", string? apiKey = null)
        => new(ModelProvider.Claude, modelId, apiKey);

    public static ModelConfig Codex(string modelId = "gpt-4o", string? apiKey = null)
        => new(ModelProvider.Codex, modelId, apiKey);

    public static ModelConfig Gemini(string modelId = "gemini-2.0-flash", string? apiKey = null)
        => new(ModelProvider.Gemini, modelId, apiKey);

    public static ModelConfig LocalDeepseek(string modelId = "deepseek-coder:6.7b")
        => new(ModelProvider.Ollama, modelId, Endpoint: "http://localhost:11434");

    public static ModelConfig LocalPhi(string modelId = "phi3.5:latest")
        => new(ModelProvider.Ollama, modelId, Endpoint: "http://localhost:11434");

    public static ModelConfig LocalQwen(string modelId = "qwen2.5-coder:7b-instruct")
        => new(ModelProvider.Ollama, modelId, Endpoint: "http://localhost:11434");

    public static ModelConfig LocalQwen2(string modelId, string endpoint = "http://localhost:11434")
        => new(ModelProvider.Ollama, modelId, Endpoint: endpoint);
}
