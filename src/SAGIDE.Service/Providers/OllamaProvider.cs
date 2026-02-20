using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Providers;

/// <summary>
/// Ollama provider that routes requests to the correct LAN server based on
/// the model's Endpoint or a pre-configured model-to-server mapping.
/// </summary>
public class OllamaProvider : IAgentProvider
{
    private readonly string _defaultBaseUrl;
    private readonly Dictionary<string, string> _modelEndpoints; // modelId -> baseUrl
    private readonly ConcurrentDictionary<string, HttpClient> _clientsByUrl = new();
    private readonly RetryPolicy _retryPolicy;
    private readonly TimeSpan _timeout;
    private readonly ILogger<OllamaProvider> _logger;

    public ModelProvider Provider => ModelProvider.Ollama;
    public int LastInputTokens { get; private set; }
    public int LastOutputTokens { get; private set; }

    public OllamaProvider(
        string defaultBaseUrl,
        Dictionary<string, string> modelEndpoints,
        RetryPolicy retryPolicy,
        TimeSpan timeout,
        ILogger<OllamaProvider> logger)
    {
        _defaultBaseUrl = defaultBaseUrl;
        _modelEndpoints = modelEndpoints;
        _retryPolicy = retryPolicy;
        _timeout = timeout;
        _logger = logger;
    }

    private HttpClient GetClient(string baseUrl) =>
        _clientsByUrl.GetOrAdd(baseUrl, url => new HttpClient
        {
            BaseAddress = new Uri(url),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        });

    private string ResolveEndpoint(ModelConfig model)
    {
        // Priority: explicit endpoint on ModelConfig > model-to-server mapping > default
        if (!string.IsNullOrEmpty(model.Endpoint))
            return model.Endpoint;
        if (_modelEndpoints.TryGetValue(model.ModelId, out var mapped))
            return mapped;
        return _defaultBaseUrl;
    }

    public async Task<string> CompleteAsync(string prompt, ModelConfig model, CancellationToken ct = default)
    {
        var endpoint = ResolveEndpoint(model);
        var client = GetClient(endpoint);
        var handler = new ResilientHttpHandler(client, _retryPolicy, _timeout, _logger);

        var requestBody = new
        {
            model = model.ModelId,
            prompt,
            stream = false,
            options = new { temperature = 0.7, num_predict = 4096 }
        };
        var json = JsonSerializer.Serialize(requestBody);

        HttpRequestMessage CreateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "api/generate");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return req;
        }

        _logger.LogDebug("Calling Ollama at {Endpoint} with model {Model}", endpoint, model.ModelId);

        var response = await handler.SendWithRetryAsync(CreateRequest, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(responseJson);
        var textContent = doc.RootElement.GetProperty("response").GetString();

        if (doc.RootElement.TryGetProperty("prompt_eval_count", out var promptTokens))
            LastInputTokens = promptTokens.GetInt32();
        if (doc.RootElement.TryGetProperty("eval_count", out var evalTokens))
            LastOutputTokens = evalTokens.GetInt32();

        _logger.LogInformation("Ollama ({Endpoint}/{Model}): {In}+{Out} tokens",
            endpoint, model.ModelId, LastInputTokens, LastOutputTokens);

        return textContent ?? string.Empty;
    }

    public async IAsyncEnumerable<string> CompleteStreamingAsync(
        string prompt, ModelConfig model, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var endpoint = ResolveEndpoint(model);
        var client = GetClient(endpoint);

        // Ollama streams NDJSON: each line is {"response":"token","done":false} until done:true
        var requestBody = new
        {
            model = model.ModelId,
            prompt,
            stream = true,
            options = new { temperature = 0.7, num_predict = 4096 }
        };
        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, "api/generate");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            string? text = null;
            bool done = false;
            try
            {
                var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("response", out var resp))
                    text = resp.GetString();
                if (doc.RootElement.TryGetProperty("done", out var doneEl))
                    done = doneEl.GetBoolean();
                // Capture eval counts from the final done=true message
                if (done)
                {
                    if (doc.RootElement.TryGetProperty("prompt_eval_count", out var pt))
                        LastInputTokens = pt.GetInt32();
                    if (doc.RootElement.TryGetProperty("eval_count", out var et))
                        LastOutputTokens = et.GetInt32();
                }
            }
            catch (JsonException) { continue; }
            if (!string.IsNullOrEmpty(text)) yield return text;
            if (done) break;
        }

        _logger.LogInformation("Ollama streaming complete ({Endpoint}/{Model}): {In}+{Out} tokens",
            ResolveEndpoint(model), model.ModelId, LastInputTokens, LastOutputTokens);
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        // Check all known servers; return true if at least one responds
        var allEndpoints = new HashSet<string>(_modelEndpoints.Values) { _defaultBaseUrl };
        foreach (var endpoint in allEndpoints)
        {
            try
            {
                var client = GetClient(endpoint);
                var response = await client.GetAsync("api/tags", ct);
                if (response.IsSuccessStatusCode)
                    return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Ollama server at {Endpoint} not available", endpoint);
            }
        }
        return false;
    }
}
