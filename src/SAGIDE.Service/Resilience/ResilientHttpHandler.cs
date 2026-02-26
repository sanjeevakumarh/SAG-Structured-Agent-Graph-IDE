using Microsoft.Extensions.Logging;

namespace SAGIDE.Service.Resilience;

/// <summary>
/// Wraps HTTP calls with retry logic, timeout enforcement, and Retry-After header support.
/// Used by all LLM providers.
/// </summary>
public class ResilientHttpHandler
{
    private readonly HttpClient _httpClient;
    private readonly RetryPolicy _retryPolicy;
    private readonly TimeSpan _timeout;
    private readonly ILogger _logger;

    public ResilientHttpHandler(
        HttpClient httpClient,
        RetryPolicy retryPolicy,
        TimeSpan timeout,
        ILogger logger)
    {
        _httpClient = httpClient;
        _retryPolicy = retryPolicy;
        _timeout = timeout;
        _logger = logger;
    }

    public int TotalAttempts { get; private set; }

    /// <summary>
    /// Sends an HTTP request with retry, backoff, and timeout enforcement.
    /// </summary>
    /// <param name="requestFactory">Factory called once per attempt (request must not be reused).</param>
    /// <param name="ct">Caller cancellation token.</param>
    /// <param name="context">
    /// Optional human-readable context string (e.g. "Claude/claude-opus-4-6") included in every
    /// log message so failures can be diagnosed without reconstructing context from surrounding lines.
    /// </param>
    public async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken ct,
        string? context = null)
    {
        var ctx = context is not null ? $"[{context}] " : string.Empty;
        HttpResponseMessage? lastResponse = null;
        Exception? lastException = null;
        TotalAttempts = 0;

        for (int attempt = 0; attempt <= _retryPolicy.MaxRetries; attempt++)
        {
            TotalAttempts = attempt + 1;
            ct.ThrowIfCancellationRequested();

            // Create a timeout-linked token for this attempt
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);

            try
            {
                var request = requestFactory();

                _logger.LogDebug(
                    "{Context}HTTP attempt {Attempt}/{Max}: {Method} {Uri}",
                    ctx, attempt + 1, _retryPolicy.MaxRetries + 1,
                    request.Method, request.RequestUri);

                lastResponse = await _httpClient.SendAsync(request, timeoutCts.Token);

                if (lastResponse.IsSuccessStatusCode)
                {
                    return lastResponse;
                }

                var statusCode = (int)lastResponse.StatusCode;

                // Non-retryable error — fail immediately
                if (!_retryPolicy.IsRetryable(statusCode))
                {
                    _logger.LogWarning(
                        "{Context}HTTP {StatusCode} is not retryable, failing immediately",
                        ctx, statusCode);
                    return lastResponse;
                }

                // Retryable — check if we have retries left
                if (attempt >= _retryPolicy.MaxRetries)
                {
                    _logger.LogWarning(
                        "{Context}HTTP {StatusCode} after all {Max} retries exhausted",
                        ctx, statusCode, _retryPolicy.MaxRetries + 1);
                    return lastResponse;
                }

                // Calculate delay — honor Retry-After header for 429
                var delay = GetRetryDelay(lastResponse, attempt);
                _logger.LogWarning(
                    "{Context}HTTP {StatusCode}, retrying in {DelayMs}ms (attempt {Attempt}/{Max})",
                    ctx, statusCode, delay.TotalMilliseconds, attempt + 1, _retryPolicy.MaxRetries + 1);

                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                // Timeout on this attempt, not user cancellation
                _logger.LogWarning(
                    "{Context}HTTP timeout after {TimeoutMs}ms (attempt {Attempt}/{Max})",
                    ctx, _timeout.TotalMilliseconds, attempt + 1, _retryPolicy.MaxRetries + 1);

                lastException = new TimeoutException(
                    $"{ctx}Provider HTTP call timed out after {_timeout.TotalMilliseconds}ms");

                if (attempt >= _retryPolicy.MaxRetries)
                    throw lastException;

                var delay = _retryPolicy.GetDelay(attempt);
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                // User cancellation — propagate immediately
                throw;
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger.LogWarning(ex,
                    "{Context}HTTP request failed (attempt {Attempt}/{Max})",
                    ctx, attempt + 1, _retryPolicy.MaxRetries + 1);

                if (attempt >= _retryPolicy.MaxRetries)
                    throw;

                var delay = _retryPolicy.GetDelay(attempt);
                await Task.Delay(delay, ct);
            }
        }

        // Should not reach here, but just in case
        if (lastResponse is not null)
            return lastResponse;

        throw lastException ?? new InvalidOperationException("Retry loop completed without result");
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        // Honor Retry-After header for rate limits
        if ((int)response.StatusCode == 429 &&
            response.Headers.TryGetValues("Retry-After", out var values))
        {
            var retryAfter = values.FirstOrDefault();
            if (retryAfter is not null && int.TryParse(retryAfter, out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }
        }

        return _retryPolicy.GetDelay(attempt);
    }
}
