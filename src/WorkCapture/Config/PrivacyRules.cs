using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkCapture.Config;

/// <summary>
/// Privacy filter configuration - what NOT to capture
/// </summary>
public class PrivacyRulesConfig
{
    public List<string> ExcludedProcesses { get; set; } = new();
    public List<string> ExcludedTitlePatterns { get; set; } = new();
    public List<string> ExcludedUrlPatterns { get; set; } = new();

    // Compiled patterns for performance
    private List<Regex>? _titleRegexes;
    private List<Regex>? _urlRegexes;
    private HashSet<string>? _processSet;

    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "config", "privacy.json");

    public static PrivacyRulesConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var config = JsonSerializer.Deserialize<PrivacyRulesConfig>(json, JsonOptions);
                if (config != null)
                {
                    config.Compile();
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to load privacy rules: {ex.Message}");
        }

        var defaults = GetDefaults();
        defaults.Compile();
        return defaults;
    }

    public void Compile()
    {
        _processSet = new HashSet<string>(
            ExcludedProcesses.Select(p => p.ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);

        _titleRegexes = ExcludedTitlePatterns
            .Select(p => GlobToRegex(p))
            .ToList();

        _urlRegexes = ExcludedUrlPatterns
            .Select(p => GlobToRegex(p))
            .ToList();
    }

    public bool IsProcessExcluded(string? processName)
    {
        if (string.IsNullOrEmpty(processName)) return false;
        return _processSet?.Contains(processName.ToLowerInvariant()) ?? false;
    }

    public bool IsTitleExcluded(string? title)
    {
        if (string.IsNullOrEmpty(title)) return false;
        return _titleRegexes?.Any(r => r.IsMatch(title)) ?? false;
    }

    public bool IsUrlExcluded(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        return _urlRegexes?.Any(r => r.IsMatch(url)) ?? false;
    }

    public (bool Excluded, string Reason) ShouldExclude(string? processName, string? title, string? url)
    {
        if (IsProcessExcluded(processName))
            return (true, $"excluded_process:{processName}");

        if (IsTitleExcluded(title))
            return (true, $"excluded_title");

        if (IsUrlExcluded(url))
            return (true, $"excluded_url");

        return (false, "");
    }

    private static Regex GlobToRegex(string pattern)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return new Regex(regexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
    }

    private static PrivacyRulesConfig GetDefaults() => new()
    {
        ExcludedProcesses = new List<string>
        {
            "1password.exe",
            "bitwarden.exe",
            "keepass.exe",
            "lastpass.exe",
            "dashlane.exe",
        },
        ExcludedTitlePatterns = new List<string>
        {
            // Banking
            "*bank*login*",
            "*chase.com*",
            "*wellsfargo.com*",
            "*bankofamerica.com*",
            "*citibank.com*",
            "*capital one*",

            // Password managers
            "*Bitwarden*Vault*",
            "*1Password*",
            "*KeePass*",
            "*LastPass*",

            // Personal email
            "*Gmail*Inbox*",
            "*Outlook*Personal*",

            // Healthcare
            "*MyChart*",
            "*patient portal*",
            "*health records*",

            // Financial
            "*Fidelity*",
            "*Vanguard*",
            "*TurboTax*",
            "*tax return*",

            // Sensitive
            "*password*visible*",
            "*show password*",
        },
        ExcludedUrlPatterns = new List<string>
        {
            "*online.chase.com*",
            "*wellsfargo.com/online*",
            "*bankofamerica.com/myaccount*",
            "*secure.*.bank*",
            "*vault.bitwarden.com*",
            "*my.1password.com*",
            "*mail.google.com*",
            "*outlook.live.com*",
            "*mychart.*",
            "*turbotax.com*",
        }
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}
