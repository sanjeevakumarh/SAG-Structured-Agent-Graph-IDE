using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Providers;

public class ProviderFactory
{
    private readonly IConfiguration _configuration;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeoutConfig _timeoutConfig;
    private readonly Dictionary<ModelProvider, IAgentProvider> _providers = new();

    public ProviderFactory(IConfiguration configuration, ILoggerFactory loggerFactory, TimeoutConfig timeoutConfig)
    {
        _configuration = configuration;
        _loggerFactory = loggerFactory;
        _timeoutConfig = timeoutConfig;
        InitializeProviders();
    }

    private void InitializeProviders()
    {
        var anthropicKey = _configuration["SAGIDE:ApiKeys:Anthropic"] ?? "";
        var openaiKey    = _configuration["SAGIDE:ApiKeys:OpenAI"] ?? "";
        var googleKey    = _configuration["SAGIDE:ApiKeys:Google"] ?? "";

        if (!string.IsNullOrEmpty(anthropicKey))
        {
            var timeout = TimeSpan.FromMilliseconds(_timeoutConfig.GetProviderTimeoutMs(ModelProvider.Claude));
            _providers[ModelProvider.Claude] = new ClaudeProvider(anthropicKey,
                new RetryPolicy { RetryableStatusCodes = [429, 500, 502, 503, 529] },
                timeout, _loggerFactory.CreateLogger<ClaudeProvider>());
        }

        if (!string.IsNullOrEmpty(openaiKey))
        {
            var timeout = TimeSpan.FromMilliseconds(_timeoutConfig.GetProviderTimeoutMs(ModelProvider.Codex));
            _providers[ModelProvider.Codex] = new CodexProvider(openaiKey, RetryPolicy.Default,
                timeout, _loggerFactory.CreateLogger<CodexProvider>());
        }

        if (!string.IsNullOrEmpty(googleKey))
        {
            var timeout = TimeSpan.FromMilliseconds(_timeoutConfig.GetProviderTimeoutMs(ModelProvider.Gemini));
            _providers[ModelProvider.Gemini] = new GeminiProvider(googleKey, RetryPolicy.Default,
                timeout, _loggerFactory.CreateLogger<GeminiProvider>());
        }

        // Multi-server Ollama: build model-to-endpoint routing table from config
        var ollamaTimeout = TimeSpan.FromMilliseconds(_timeoutConfig.GetProviderTimeoutMs(ModelProvider.Ollama));
        var modelEndpoints = BuildOllamaRoutingTable();
        var defaultServer = _configuration["SAGIDE:Ollama:DefaultServer"] ?? "http://localhost:11434";

        _providers[ModelProvider.Ollama] = new OllamaProvider(
            defaultServer, modelEndpoints, RetryPolicy.Default, ollamaTimeout,
            _loggerFactory.CreateLogger<OllamaProvider>());
    }

    private Dictionary<string, string> BuildOllamaRoutingTable()
    {
        var table = new Dictionary<string, string>();
        var serversSection = _configuration.GetSection("SAGIDE:Ollama:Servers");
        foreach (var server in serversSection.GetChildren())
        {
            var baseUrl = server["BaseUrl"] ?? "";
            var modelsSection = server.GetSection("Models");
            foreach (var modelEntry in modelsSection.GetChildren())
            {
                var modelId = modelEntry.Value ?? "";
                if (!string.IsNullOrEmpty(modelId) && !string.IsNullOrEmpty(baseUrl))
                    table[modelId] = baseUrl;
            }
        }
        return table;
    }

    public IAgentProvider? GetProvider(ModelProvider provider)
    {
        _providers.TryGetValue(provider, out var agentProvider);
        return agentProvider;
    }

    public IEnumerable<IAgentProvider> GetAllProviders() => _providers.Values;
    public IReadOnlyList<ModelProvider> GetAvailableProviders() => _providers.Keys.ToList();
}
