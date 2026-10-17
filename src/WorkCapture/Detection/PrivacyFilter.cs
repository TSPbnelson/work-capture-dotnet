using System.Text.RegularExpressions;
using WorkCapture.Config;

namespace WorkCapture.Detection;

/// <summary>
/// Filters out sensitive content from capture
/// </summary>
public class PrivacyFilter
{
    private readonly PrivacyRulesConfig _rules;

    // Whole words only (v1.10.0). Until then this was a substring match that also contained
    // "login", "signin", "sign-in" and "account", so Entra "Sign-in logs", any "Account" page and
    // even afterbankruptcy.com ("bank") became metadata-only: no screenshot, and recorded at most
    // hourly. Billing time no longer depends on screenshots (activity stream), but the evidence
    // of WHO and WHAT does. Credentials typed or shown are handled by WorkCapture.Privacy.
    private static readonly Regex SensitiveWords = new(
        @"\b(password|bank|credit card|ssn|social security)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
        return SensitiveWords.IsMatch($"{windowTitle ?? ""} {url ?? ""}");
    }
}
