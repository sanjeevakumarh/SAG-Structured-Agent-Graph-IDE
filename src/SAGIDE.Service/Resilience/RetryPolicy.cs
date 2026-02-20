namespace SAGIDE.Service.Resilience;

public enum BackoffStrategy { Fixed, Exponential }

public class RetryPolicy
{
    public int MaxRetries { get; init; } = 3;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public BackoffStrategy Strategy { get; init; } = BackoffStrategy.Exponential;
    public int[] RetryableStatusCodes { get; init; } = [429, 500, 502, 503, 529];

    public TimeSpan GetDelay(int attempt)
    {
        return Strategy switch
        {
            BackoffStrategy.Exponential => InitialDelay * Math.Pow(2, attempt),
            BackoffStrategy.Fixed => InitialDelay,
            _ => InitialDelay
        };
    }

    public bool IsRetryable(int statusCode) => RetryableStatusCodes.Contains(statusCode);

    public static RetryPolicy Default => new();

    public static RetryPolicy ForOllama => new()
    {
        MaxRetries = 2,
        InitialDelay = TimeSpan.FromSeconds(2),
        Strategy = BackoffStrategy.Fixed,
        RetryableStatusCodes = [500, 502, 503]
    };
}
