using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SAGIDE.Core.Models;
using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

// ── Fake HTTP message handler ─────────────────────────────────────────────────

/// <summary>
/// Controls HTTP responses programmatically for testing provider error paths.
/// Call <see cref="Enqueue"/> to queue responses in order; remaining calls receive
/// the last enqueued response (or 200 OK if empty).
/// </summary>
internal sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public int RequestCount { get; private set; }

    public void Enqueue(HttpResponseMessage response)
        => _responses.Enqueue(response);

    public void EnqueueMany(HttpStatusCode code, int count)
    {
        for (int i = 0; i < count; i++)
            _responses.Enqueue(new HttpResponseMessage(code));
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        RequestCount++;
        cancellationToken.ThrowIfCancellationRequested();
        var response = _responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") };
        return Task.FromResult(response);
    }
}

// ── Helper factory ────────────────────────────────────────────────────────────

internal static class ResilientHandlerFactory
{
    public static (ResilientHttpHandler handler, FakeHttpMessageHandler fake) Create(
        int maxRetries = 2,
        BackoffStrategy strategy = BackoffStrategy.Fixed,
        TimeSpan? initialDelay = null,
        TimeSpan? timeout = null)
    {
        var fake = new FakeHttpMessageHandler();
        var httpClient = new HttpClient(fake) { BaseAddress = new Uri("https://test.example.com/") };

        var policy = new RetryPolicy
        {
            MaxRetries   = maxRetries,
            Strategy     = strategy,
            InitialDelay = initialDelay ?? TimeSpan.FromMilliseconds(1), // fast for tests
        };

        var handler = new ResilientHttpHandler(
            httpClient,
            policy,
            timeout ?? TimeSpan.FromSeconds(30),
            NullLogger.Instance);

        return (handler, fake);
    }
}

// ── ResilientHttpHandler tests ────────────────────────────────────────────────

public class ProviderErrorPathTests
{
    // ── 429 Rate-limit retried ────────────────────────────────────────────────

    [Fact]
    public async Task ResilientHandler_429_RetriesAndEventuallySucceeds()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 2);

        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true}") });

        var response = await handler.SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/v1/test"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(3, fake.RequestCount); // 2 failures + 1 success
        Assert.Equal(3, handler.TotalAttempts);
    }

    [Fact]
    public async Task ResilientHandler_429_RespectsRetryAfterHeader()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 1);

        var rateLimitResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        rateLimitResponse.Headers.Add("Retry-After", "1"); // 1 second
        fake.Enqueue(rateLimitResponse);
        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        // Should not throw — delay honored (1s in this case, but test doesn't verify timing)
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var response = await handler.SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/v1/test"),
            cts.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fake.RequestCount);
    }

    [Fact]
    public async Task ResilientHandler_429_ExhaustsRetries_ReturnsLastResponse()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 2);

        fake.EnqueueMany(HttpStatusCode.TooManyRequests, 3); // all fail

        var response = await handler.SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/v1/test"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(3, fake.RequestCount);
    }

    // ── 500 Server error retried ──────────────────────────────────────────────

    [Fact]
    public async Task ResilientHandler_500_RetriesAndSucceeds()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 1);

        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var response = await handler.SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/v1/test"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fake.RequestCount);
    }

    // ── 400 Bad request NOT retried ───────────────────────────────────────────

    [Fact]
    public async Task ResilientHandler_400_NotRetried_FailsImmediately()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 3);

        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.BadRequest));

        var response = await handler.SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/v1/test"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, fake.RequestCount); // only one attempt, no retries
    }

    [Fact]
    public async Task ResilientHandler_401_NotRetried()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 3);

        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        var response = await handler.SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/v1/test"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, fake.RequestCount);
    }

    [Fact]
    public async Task ResilientHandler_404_NotRetried()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 3);

        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));

        var response = await handler.SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/v1/test"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(1, fake.RequestCount);
    }

    // ── 503 Service unavailable retried ──────────────────────────────────────

    [Fact]
    public async Task ResilientHandler_503_IsRetried()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 1);

        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        var response = await handler.SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/v1/test"),
            CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, fake.RequestCount);
    }

    // ── Cancellation propagated ───────────────────────────────────────────────

    [Fact]
    public async Task ResilientHandler_UserCancellation_Propagates()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 3);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // immediately cancelled

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            handler.SendWithRetryAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "/v1/test"),
                cts.Token));
    }

    // ── Exponential backoff delay doubles ─────────────────────────────────────

    [Fact]
    public void RetryPolicy_Exponential_DelayDoubles()
    {
        var policy = new RetryPolicy
        {
            Strategy     = BackoffStrategy.Exponential,
            InitialDelay = TimeSpan.FromSeconds(1),
        };

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(2));
        Assert.Equal(TimeSpan.FromSeconds(8), policy.GetDelay(3));
    }

    [Fact]
    public void RetryPolicy_Fixed_DelayConstant()
    {
        var policy = new RetryPolicy
        {
            Strategy     = BackoffStrategy.Fixed,
            InitialDelay = TimeSpan.FromMilliseconds(500),
        };

        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.GetDelay(5));
        Assert.Equal(TimeSpan.FromMilliseconds(500), policy.GetDelay(10));
    }

    // ── TotalAttempts tracking ────────────────────────────────────────────────

    [Fact]
    public async Task ResilientHandler_TotalAttempts_TracksCorrectly()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 2);

        fake.EnqueueMany(HttpStatusCode.InternalServerError, 3);

        await handler.SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/test"),
            CancellationToken.None);

        Assert.Equal(3, handler.TotalAttempts);
    }

    [Fact]
    public async Task ResilientHandler_FirstAttemptSucceeds_TotalAttemptsIsOne()
    {
        var (handler, fake) = ResilientHandlerFactory.Create(maxRetries: 3);

        fake.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });

        await handler.SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Post, "/test"),
            CancellationToken.None);

        Assert.Equal(1, handler.TotalAttempts);
    }
}

// ── RetryPolicy.IsRetryable tests (separate from existing RetryPolicyTests) ───

public class RetryPolicyIsRetryableTests
{
    [Theory]
    [InlineData(429, true)]   // rate limit — retryable
    [InlineData(500, true)]   // server error — retryable
    [InlineData(503, true)]   // service unavailable — retryable
    [InlineData(400, false)]  // bad request — not retryable
    [InlineData(401, false)]  // unauthorized — not retryable
    [InlineData(403, false)]  // forbidden — not retryable
    [InlineData(404, false)]  // not found — not retryable
    [InlineData(422, false)]  // unprocessable — not retryable
    public void IsRetryable_MapsStatusCodesCorrectly(int statusCode, bool expected)
    {
        Assert.Equal(expected, RetryPolicy.Default.IsRetryable(statusCode));
    }
}
