using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Providers;

public class GeminiProvider : IAgentProvider
{
    private readonly HttpClient _httpClient;
    private readonly ResilientHttpHandler _resilientHandler;
    private readonly ILogger<GeminiProvider> _logger;
    private readonly string _apiKey;

    public ModelProvider Provider => ModelProvider.Gemini;
    public int LastInputTokens { get; private set; }
    public int LastOutputTokens { get; private set; }

    public GeminiProvider(string apiKey, RetryPolicy retryPolicy, TimeSpan timeout, ILogger<GeminiProvider> logger)
    {
        _apiKey = apiKey;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/"),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };

        _resilientHandler = new ResilientHttpHandler(_httpClient, retryPolicy, timeout, logger);
    }

    public async Task<string> CompleteAsync(string prompt, ModelConfig model, CancellationToken ct = default)
    {
        var requestBody = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            },
            generationConfig = new { maxOutputTokens = 4096 }
        };
        var json = JsonSerializer.Serialize(requestBody);
        var url = $"v1beta/models/{model.ModelId}:generateContent?key={_apiKey}";

        HttpRequestMessage CreateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return req;
        }

        _logger.LogDebug("Calling Gemini API with model {Model}", model.ModelId);

        var response = await _resilientHandler.SendWithRetryAsync(CreateRequest, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(responseJson);
        var textContent = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString();

        // Extract token usage from usageMetadata
        if (doc.RootElement.TryGetProperty("usageMetadata", out var usage))
        {
            LastInputTokens = usage.TryGetProperty("promptTokenCount", out var inp) ? inp.GetInt32() : 0;
            LastOutputTokens = usage.TryGetProperty("candidatesTokenCount", out var outp) ? outp.GetInt32() : 0;

            _logger.LogInformation(
                "Gemini API: {InputTokens} input + {OutputTokens} output tokens ({Attempts} attempt(s))",
                LastInputTokens, LastOutputTokens, _resilientHandler.TotalAttempts);
        }
        else
        {
            _logger.LogDebug("Gemini API responded ({Attempts} attempt(s))", _resilientHandler.TotalAttempts);
        }

        return textContent ?? string.Empty;
    }

    public async IAsyncEnumerable<string> CompleteStreamingAsync(
        string prompt, ModelConfig model, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = new
        {
            contents = new[] { new { parts = new[] { new { text = prompt } } } },
            generationConfig = new { maxOutputTokens = 4096 }
        };
        var json = JsonSerializer.Serialize(requestBody);
        var url = $"v1beta/models/{model.ModelId}:streamGenerateContent?alt=sse&key={_apiKey}";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            string? text = null;
            try
            {
                var doc = JsonDocument.Parse(data);
                var candidates = doc.RootElement.GetProperty("candidates");
                if (candidates.GetArrayLength() > 0)
                    text = candidates[0].GetProperty("content").GetProperty("parts")[0]
                        .GetProperty("text").GetString();
            }
            catch (JsonException) { continue; }
            if (!string.IsNullOrEmpty(text)) yield return text;
        }

        _logger.LogInformation("Gemini streaming complete (model {Model})", model.ModelId);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return Task.FromResult(!string.IsNullOrEmpty(_apiKey));
    }
}
