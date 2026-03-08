namespace SAGIDE.Service.Rag;

/// <summary>
/// Scores search result quality on a 0.0–1.0 scale.
/// Detects captcha pages, bot walls, empty results, and low-content responses.
/// Used by <see cref="WebSearchAdapter"/> to decide whether to accept fresh results
/// or fall back to a cached (possibly stale) version.
/// </summary>
public static class SearchQualityScorer
{
    /// <summary>Minimum quality score to accept fresh results over stale cache.</summary>
    public const double AcceptThreshold = 0.3;

    private static readonly string[] BadPatterns =
    [
        "captcha", "verify you are human", "verify you're human",
        "access denied", "403 forbidden", "401 unauthorized",
        "enable javascript", "enable cookies", "cookie consent",
        "cloudflare", "checking your browser", "just a moment",
        "please complete the security check", "bot detection",
        "rate limit exceeded", "too many requests",
    ];

    /// <summary>
    /// Scores search result text. Returns (score, reason).
    /// <list type="bullet">
    ///   <item>0.0 = garbage (captcha, empty, bot wall)</item>
    ///   <item>0.1–0.4 = low quality (very short, few results)</item>
    ///   <item>0.5–1.0 = acceptable to good</item>
    /// </list>
    /// </summary>
    public static (double Score, string Reason) Score(string resultText, int resultCount)
    {
        if (string.IsNullOrWhiteSpace(resultText))
            return (0.0, "empty_results");

        // Check for bot/captcha patterns
        var lower = resultText.ToLowerInvariant();
        foreach (var pattern in BadPatterns)
        {
            if (lower.Contains(pattern))
                return (0.0, $"bad_pattern:{pattern}");
        }

        if (resultCount == 0)
            return (0.0, "zero_results");

        // Very short content suggests blocked/truncated responses
        var avgCharsPerResult = resultText.Length / Math.Max(resultCount, 1);
        if (avgCharsPerResult < 30)
            return (0.2, "very_short_snippets");

        // Few results
        if (resultCount == 1)
            return (0.4, "single_result");

        if (resultCount <= 2)
            return (0.5, "few_results");

        // Content length scoring
        if (resultText.Length < 200)
            return (0.4, "low_total_content");

        // Good results
        if (resultCount >= 5 && resultText.Length > 500)
            return (1.0, "good");

        return (0.7, "acceptable");
    }
}
