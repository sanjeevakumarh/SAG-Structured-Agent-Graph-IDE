using SAGIDE.Service.Resilience;

namespace SAGIDE.Service.Tests;

public class RetryPolicyTests
{
    [Fact]
    public void ExponentialBackoff_DoublesDelay()
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Exponential,
            InitialDelay = TimeSpan.FromSeconds(1)
        };

        Assert.Equal(TimeSpan.FromSeconds(1), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.GetDelay(2));
    }

    [Fact]
    public void FixedBackoff_SameDelay()
    {
        var policy = new RetryPolicy
        {
            Strategy = BackoffStrategy.Fixed,
            InitialDelay = TimeSpan.FromSeconds(2)
        };

        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(0));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(1));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.GetDelay(2));
    }

    [Fact]
    public void IsRetryable_429_ReturnsTrue()
    {
        var policy = RetryPolicy.Default;
        Assert.True(policy.IsRetryable(429));
        Assert.True(policy.IsRetryable(500));
        Assert.True(policy.IsRetryable(503));
    }

    [Fact]
    public void IsRetryable_400_ReturnsFalse()
    {
        var policy = RetryPolicy.Default;
        Assert.False(policy.IsRetryable(400));
        Assert.False(policy.IsRetryable(401));
        Assert.False(policy.IsRetryable(404));
    }
}
