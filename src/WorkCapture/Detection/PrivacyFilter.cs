using WorkCapture.Config;

namespace WorkCapture.Detection;

/// <summary>
/// Filters out sensitive content from capture
/// </summary>
public class PrivacyFilter
{
    private readonly PrivacyRulesConfig _rules;

    private static readonly HashSet<string> SensitiveKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "password", "login", "signin", "sign-in",
        "bank", "account", "credit card",
        "ssn", "social security"
    };

    public PrivacyFilter(PrivacyRulesConfig rules)
    {
        _rules = rules;
    }

    /// <summary>
    /// Check if content should be excluded from capture
    /// </summary>
    public (bool Excluded, string Reason) ShouldExclude(
        string? processName,
        string? windowTitle,
        string? url)
    {
        return _rules.ShouldExclude(processName, windowTitle, url);
    }

    /// <summary>
    /// Quick check if content appears sensitive (for metadata-only decision)
    /// </summary>
    public bool IsSensitiveContent(string? windowTitle, string? url)
    {
        var text = $"{windowTitle ?? ""} {url ?? ""}".ToLowerInvariant();

        return SensitiveKeywords.Any(keyword => text.Contains(keyword));
    }
}
