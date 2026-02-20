using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Providers;

public class CodexProvider : IAgentProvider
{
    private readonly HttpClient _httpClient;
    private readonly ResilientHttpHandler _resilientHandler;
    private readonly ILogger<CodexProvider> _logger;
    private readonly string _apiKey;

    public ModelProvider Provider => ModelProvider.Codex;
    public int LastInputTokens { get; private set; }
    public int LastOutputTokens { get; private set; }

    public CodexProvider(string apiKey, RetryPolicy retryPolicy, TimeSpan timeout, ILogger<CodexProvider> logger)
    {
        _apiKey = apiKey;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/"),
            Timeout = System.Threading.Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);

        _resilientHandler = new ResilientHttpHandler(_httpClient, retryPolicy, timeout, logger);
    }

    public async Task<string> CompleteAsync(string prompt, ModelConfig model, CancellationToken ct = default)
    {
        var requestBody = new
        {
            model = model.ModelId,
            max_tokens = 4096,
            messages = new[] { new { role = "user", content = prompt } }
        };
        var json = JsonSerializer.Serialize(requestBody);

        HttpRequestMessage CreateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return req;
        }

        _logger.LogDebug("Calling OpenAI API with model {Model}", model.ModelId);

        var response = await _resilientHandler.SendWithRetryAsync(CreateRequest, ct);
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var doc = JsonDocument.Parse(responseJson);
        var textContent = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();

        // Extract token usage
        if (doc.RootElement.TryGetProperty("usage", out var usage))
        {
            LastInputTokens = usage.TryGetProperty("prompt_tokens", out var inp) ? inp.GetInt32() : 0;
            LastOutputTokens = usage.TryGetProperty("completion_tokens", out var outp) ? outp.GetInt32() : 0;

            _logger.LogInformation(
                "OpenAI API: {InputTokens} input + {OutputTokens} output tokens ({Attempts} attempt(s))",
                LastInputTokens, LastOutputTokens, _resilientHandler.TotalAttempts);
        }
        else
        {
            _logger.LogDebug("OpenAI API responded ({Attempts} attempt(s))", _resilientHandler.TotalAttempts);
        }

        return textContent ?? string.Empty;
    }

    public async IAsyncEnumerable<string> CompleteStreamingAsync(
        string prompt, ModelConfig model, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var requestBody = new
        {
            model = model.ModelId,
            max_tokens = 4096,
            stream = true,
            messages = new[] { new { role = "user", content = prompt } }
        };
        var json = JsonSerializer.Serialize(requestBody);
        var request = new HttpRequestMessage(HttpMethod.Post, "v1/chat/completions");
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
            if (data == "[DONE]") break;
            string? text = null;
            try
            {
                var doc = JsonDocument.Parse(data);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("delta", out var delta) &&
                    delta.TryGetProperty("content", out var content))
                    text = content.GetString();
            }
            catch (JsonException) { continue; }
            if (!string.IsNullOrEmpty(text)) yield return text;
        }

        _logger.LogInformation("OpenAI streaming complete (model {Model})", model.ModelId);
    }

    public Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        return Task.FromResult(!string.IsNullOrEmpty(_apiKey));
    }
}
