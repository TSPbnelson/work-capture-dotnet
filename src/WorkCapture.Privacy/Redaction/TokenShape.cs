using System.Text.RegularExpressions;

namespace WorkCapture.Privacy.Redaction;

/// <summary>
/// Classifies a single whitespace-free token: how many character classes it uses, its Shannon
/// entropy, and whether it has a "known safe" shape (IP, FQDN, path, GUID...) that is useful
/// billing evidence and is not a credential.
///
/// The safe-shape list is deliberately short. Every entry is a hole in the password-shape net,
/// so a shape only goes here if (a) it carries client-identification value and (b) people do
/// not choose passwords that look like it.
/// </summary>
public static class TokenShape
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);

    private static Regex R(string p) => new(p, RegexOptions.CultureInvariant, Timeout);

    private static readonly Regex[] SafeShapes =
    {
        R(@"^\d{1,3}(\.\d{1,3}){3}(/\d{1,2})?(:\d{1,5})?$"),                 // IPv4[/cidr][:port]
        R(@"^[a-zA-Z][a-zA-Z0-9+.-]*://[^\s:@/]+(:\d+)?(/\S*)?$"),            // URL WITHOUT user:pass@
        R(@"^[^@\s:/]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[a-z]{2,24}$"),       // email / UPN
        R(@"^[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[a-z]{2,24}$"),                 // FQDN (lowercase TLD)
        R(@"^[A-Za-z_][A-Za-z0-9._-]{0,31}@(\d{1,3}(\.\d{1,3}){3}|[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[a-z]{2,24})(:\d{1,5})?$"), // ssh user@host
        R(@"^/(user|u|domain|computer|server|s):[A-Za-z0-9.\\@_-]+$"),     // /user:CORP\admin (never /p:)
        R(@"^([A-Za-z]:\\|\\\\)"),                                             // C:\..., \\server\share
        R(@"^~?/[\w./-]*$"),                                                   // /etc/nginx, ~/x
        R(@"^[A-Za-z0-9.-]{1,15}\\[A-Za-z0-9._-]+$"),                          // DOMAIN\user
        R(@"^\{?[0-9a-fA-F]{8}-([0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}\}?$"),     // GUID (tenant/object IDs)
        R(@"^v?\d+(\.\d+){1,3}$"),                                             // version 10.0.19041
        R(@"^[A-Z][a-z]+-[A-Z][A-Za-z]+$"),                                    // PowerShell Verb-Noun
        R(@"^-{1,2}[A-Za-z][A-Za-z-]*$"),                                      // -Identity, --dry-run
        R(@"^([0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}$"),                         // MAC address
    };

    /// <summary>Number of distinct classes among lower, upper, digit, symbol (0-4).</summary>
    public static int CharClassCount(string token)
    {
        bool lower = false, upper = false, digit = false, symbol = false;
        foreach (var c in token)
        {
            if (char.IsLower(c)) lower = true;
            else if (char.IsUpper(c)) upper = true;
            else if (char.IsDigit(c)) digit = true;
            else if (!char.IsWhiteSpace(c)) symbol = true;
        }
        return (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0);
    }

    /// <summary>Shannon entropy in bits per character.</summary>
    public static double ShannonEntropy(string token)
    {
        if (token.Length == 0) return 0;
        var counts = new Dictionary<char, int>();
        foreach (var c in token)
            counts[c] = counts.TryGetValue(c, out var n) ? n + 1 : 1;

        double h = 0;
        foreach (var n in counts.Values)
        {
            var p = (double)n / token.Length;
            h -= p * Math.Log2(p);
        }
        return h;
    }

    public static bool IsSafeShape(string token, ISet<string>? safeTerms = null)
    {
        if (safeTerms != null && safeTerms.Contains(token)) return true;
        foreach (var r in SafeShapes)
            if (r.IsMatch(token)) return true;
        return false;
    }

    /// <summary>
    /// Looks like a human-chosen password: 8-64 chars, at least 3 of 4 character classes,
    /// and not a known safe shape. Short typed passwords defeat entropy tests (a 10-char
    /// string can never exceed log2(10) = 3.3 bits/char), so composition is the signal.
    /// </summary>
    public static bool IsPasswordShaped(string token, ISet<string>? safeTerms = null) =>
        token.Length is >= 8 and <= 64
        && CharClassCount(token) >= 3
        && !IsSafeShape(token, safeTerms);

    private static bool IsAllIn(string token, string alphabet)
    {
        foreach (var c in token)
            if (alphabet.IndexOf(c) < 0) return false;
        return true;
    }

    private const string Hex = "0123456789abcdefABCDEF";
    private const string Base64 =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=_-~.";

    /// <summary>
    /// Machine-generated secret test, thresholds from Yelp detect-secrets: base64-alphabet
    /// strings at 4.5 bits/char, hex strings at 3.0 bits/char. Minimum length 20 (hex 32,
    /// so MD5-length and longer API keys are caught but short hex like colour codes are not).
    /// </summary>
    public static bool IsHighEntropySecret(string token, ISet<string>? safeTerms = null)
    {
        if (token.Length < 20 || IsSafeShape(token, safeTerms)) return false;
        if (IsAllIn(token, Hex))
            return token.Length >= 32 && ShannonEntropy(token) >= 3.0;
        if (IsAllIn(token, Base64))
            return ShannonEntropy(token) >= 4.5 || (token.Length >= 32 && CharClassCount(token) >= 3
                                                  && ShannonEntropy(token) >= 4.0);
        return false;
    }
}
