using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SAGIDE.Core.Interfaces;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Providers;

/// <summary>
/// Template-method base class for single-endpoint HTTP providers (Claude, Gemini).
/// Handles HttpClient lifecycle, retry/timeout, token tracking, and SSE streaming loop.
/// Subclasses supply the provider-specific request/response shapes via abstract methods.
/// Multi-endpoint providers (Codex, Ollama) keep their own routing logic and do not extend this.
/// </summary>
public abstract class BaseHttpAgentProvider : IAgentProvider
{
    protected readonly HttpClient _httpClient;
    protected readonly ResilientHttpHandler _resilientHandler;
    protected readonly ILogger _logger;
    protected readonly int _maxTokens;
    private readonly bool _isConfigured;
    private readonly TimeSpan _streamingTimeout;

    public abstract ModelProvider Provider { get; }
    public int LastInputTokens { get; protected set; }
    public int LastOutputTokens { get; protected set; }

    protected BaseHttpAgentProvider(
        string baseUrl,
        IReadOnlyDictionary<string, string> defaultHeaders,
        RetryPolicy retryPolicy,
        TimeSpan timeout,
        ILogger logger,
        bool isConfigured = true,
        int maxTokens = 4096)
    {
        _logger            = logger;
        _isConfigured      = isConfigured;
        _maxTokens         = maxTokens;
        _streamingTimeout  = timeout;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout     = System.Threading.Timeout.InfiniteTimeSpan // managed by ResilientHttpHandler
        };
        foreach (var (key, value) in defaultHeaders)
            _httpClient.DefaultRequestHeaders.Add(key, value);

        _resilientHandler = new ResilientHttpHandler(_httpClient, retryPolicy, timeout, logger);
    }

    // ── Abstract contract ─────────────────────────────────────────────────────

    /// <summary>Relative endpoint path for non-streaming completions.</summary>
    protected abstract string GetCompletionEndpoint(ModelConfig model);

    /// <summary>
    /// Relative endpoint path for streaming completions.
    /// Defaults to <see cref="GetCompletionEndpoint"/>; override when the URL differs (e.g. Gemini).
    /// </summary>
    protected virtual string GetStreamingEndpoint(ModelConfig model) => GetCompletionEndpoint(model);

    /// <summary>JSON-serializable request body for a non-streaming call.</summary>
    protected abstract object BuildRequestBody(string prompt, ModelConfig model);

    /// <summary>JSON-serializable request body for a streaming call.</summary>
    protected abstract object BuildStreamingRequestBody(string prompt, ModelConfig model);

    /// <summary>Extracts the text content from a parsed non-streaming response.</summary>
    protected abstract string? ExtractContent(JsonDocument response);

    /// <summary>Reads token counts from a parsed non-streaming response into LastInputTokens / LastOutputTokens.</summary>
    protected abstract void ExtractTokenUsage(JsonDocument response);

    /// <summary>
    /// Extracts the incremental text from one SSE "data: {...}" JSON document.
    /// Return null to skip the chunk (keep-alive, metadata lines, etc.).
    /// </summary>
    protected abstract string? ExtractStreamDelta(JsonDocument doc);

    /// <summary>
    /// Returns true when the SSE data line signals end-of-stream.
    /// Claude overrides this to return <c>data == "[DONE]"</c>; Gemini relies on EOF.
    /// </summary>
    protected virtual bool IsStreamDone(string dataLine) => false;

    // ── IAgentProvider implementation ─────────────────────────────────────────

    public async Task<string> CompleteAsync(string prompt, ModelConfig model, CancellationToken ct = default)
    {
        var endpoint = GetCompletionEndpoint(model);
        var json     = JsonSerializer.Serialize(BuildRequestBody(prompt, model));

        HttpRequestMessage CreateRequest()
        {
            var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            return req;
        }

        _logger.LogDebug("Calling {Provider} with model {Model}", Provider, model.ModelId);

        var response = await _resilientHandler.SendWithRetryAsync(
            CreateRequest, ct, context: $"{Provider}/{model.ModelId}");
        response.EnsureSuccessStatusCode();

        var responseJson = await response.Content.ReadAsStringAsync(ct);
        var doc          = JsonDocument.Parse(responseJson);

        ExtractTokenUsage(doc);
        var content = ExtractContent(doc);

        _logger.LogInformation("{Provider}: {In}+{Out} tokens ({Attempts} attempt(s))",
            Provider, LastInputTokens, LastOutputTokens, _resilientHandler.TotalAttempts);

        return content ?? string.Empty;
    }

    public async IAsyncEnumerable<string> CompleteStreamingAsync(
        string prompt, ModelConfig model, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var endpoint = GetStreamingEndpoint(model);
        var json     = JsonSerializer.Serialize(BuildStreamingRequestBody(prompt, model));

        // Apply the same timeout budget as non-streaming calls.
        using var timeoutCts = new CancellationTokenSource(_streamingTimeout);
        using var linkedCts  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var token = linkedCts.Token;

        _logger.LogDebug("[{Provider}/{Model}] Streaming request started", Provider, model.ModelId);

        var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.LogError("[{Provider}/{Model}] Streaming timed out after {TimeoutMs}ms",
                Provider, model.ModelId, (int)_streamingTimeout.TotalMilliseconds);
            throw new TimeoutException(
                $"{Provider}/{model.ModelId} streaming timed out after {(int)_streamingTimeout.TotalMilliseconds}ms");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "[{Provider}/{Model}] Streaming request failed", Provider, model.ModelId);
            throw;
        }

        using var stream = await response.Content.ReadAsStreamAsync(token);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(token)) != null)
        {
            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (IsStreamDone(data)) break;
            string? text = null;
            try
            {
                var doc = JsonDocument.Parse(data);
                text = ExtractStreamDelta(doc);
            }
            catch (JsonException) { continue; }
            if (!string.IsNullOrEmpty(text)) yield return text;
        }

        _logger.LogInformation("[{Provider}/{Model}] Streaming complete", Provider, model.ModelId);
    }

    public virtual Task<bool> IsAvailableAsync(CancellationToken ct = default)
        => Task.FromResult(_isConfigured);
}
