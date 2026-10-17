using System.Text;
using System.Text.RegularExpressions;

namespace WorkCapture.Privacy.Redaction;

/// <summary>
/// How aggressive to be.
///   General   — screen text (OCR, UI Automation, window titles). Pattern + entropy detectors only.
///               Screen text is full of model numbers and hostnames that look password-ish, and it
///               is the main client-identification signal, so the composition heuristic stays off.
///   Keystrokes — typed text. Adds the password-shape heuristic to every token, because a typed
///               password is a bare token with no label next to it.
/// </summary>
public enum RedactionMode { General, Keystrokes }

public sealed record RedactionResult(string Text, IReadOnlyList<string> Kinds)
{
    public bool WasRedacted => Kinds.Count > 0;
}

public interface ITextRedactor
{
    RedactionResult Redact(string? text, RedactionMode mode);
}

/// <summary>
/// Masks credentials, tokens, keys, card numbers and SSNs in free text.
///
/// FAIL-CLOSED: any exception (including a regex timeout) returns a single
/// <c>[REDACTED:filter_error]</c> instead of the input. The caller can never receive
/// unfiltered text from this class.
///
/// Masks are fixed tokens (<c>[REDACTED:kind]</c>) so the secret's length is not leaked.
/// Pattern sources: gitleaks default rules, GitHub secret-scanning prefixes, detect-secrets
/// entropy limits, Presidio's Luhn-validated card recogniser, plus MSP-specific CLI forms
/// (FortiGate/Cisco/net user/PowerShell) seen in day-to-day client work.
/// </summary>
public sealed class SecretRedactor : ITextRedactor
{
    public const string FilterErrorMask = "[REDACTED:filter_error]";
    public static string Mask(string kind) => $"[REDACTED:{kind}]";

    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);
    private const RegexOptions Opt = RegexOptions.CultureInvariant;
    private const RegexOptions OptI = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    /// <summary>
    /// A detector masks either the whole match or, when the pattern has a group named
    /// <c>v</c>, only that group (so "password=" stays readable and only the value goes).
    /// </summary>
    private sealed record Detector(string Kind, Regex Pattern, Func<string, bool>? Validate = null);

    private static Detector D(string kind, string pattern, RegexOptions opt = Opt,
                              Func<string, bool>? validate = null)
        => new(kind, new Regex(pattern, opt, Timeout), validate);

    // Shared value form: quoted string or a run of non-space.
    private const string Val = @"(?<v>""[^""]*""|'[^']*'|[^\s""']+)";

    private static readonly Detector[] Detectors =
    {
        // --- Structured secrets -----------------------------------------------------------
        D("private_key", @"-----BEGIN [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----[\s\S]*?(-----END [A-Z0-9 ]*PRIVATE KEY( BLOCK)?-----|$)"),
        D("url_credentials", @"\b[a-zA-Z][a-zA-Z0-9+.-]*://[^\s:/@]+:(?<v>[^\s@/]+)@"),
        D("jwt", @"\beyJ[A-Za-z0-9_-]{8,}\.eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]*"),

        // --- Vendor token prefixes (GitHub secret scanning / gitleaks) ----------------------
        D("aws_access_key", @"\b(A3T[A-Z0-9]|AKIA|ASIA|ABIA|ACCA|AGPA|AIDA|AROA|ANPA|ANVA|AIPA)[0-9A-Z]{16}\b"),
        D("azure_client_secret", @"(?<![A-Za-z0-9_~.])[A-Za-z0-9_~.]{3}\dQ~[A-Za-z0-9_~.-]{31,34}"),
        D("github_token", @"\b(gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{22,})\b"),
        D("slack_token", @"\bxox[abposr]-[A-Za-z0-9-]{10,}"),
        D("slack_webhook", @"https://hooks\.slack\.com/services/\S+"),
        D("api_key", @"\bsk-(ant-|proj-)?[A-Za-z0-9_-]{20,}"),                // Anthropic / OpenAI
        D("stripe_key", @"\b[rs]k_(live|test)_[A-Za-z0-9]{16,}"),
        D("google_api_key", @"\bAIza[0-9A-Za-z_-]{35}"),
        D("bearer_token", @"\b(Bearer|Basic)\s+(?<v>[A-Za-z0-9._~+/=-]{12,})", OptI),

        // --- Labelled values: password=..., "client_secret": "...", AccountKey=... ----------
        D("labelled_secret",
          @"(?<![A-Za-z])[""']?(pass(word|wd|phrase)?|pwd|secret|client[_-]?secret|token|api[_-]?key|access[_-]?key|"
          + @"private[_-]?key|auth(orization)?|credentials?|pin|psk|pre-?shared-?key|shared[_-]?secret|"
          + @"accountkey|sharedaccesskey|sharedaccesssignature|sig|community)[""']?\s*[:=]\s*"
          + @"((Bearer|Basic)\s+)?" + Val, OptI),

        // --- Command-line forms ------------------------------------------------------------
        // -Password X, --password=X, -NewPassword X, /p:X, -ClientSecret X, --api-key X
        D("cli_secret_flag",
          @"(?<=^|\s)(--?[A-Za-z]*(password|passwd|passphrase|secret|token|api-?key)|/p(assword)?:)(\s*=\s*|\s+|(?<=:))" + Val, OptI),
        D("cli_secret_flag", @"\bmysql(dump|admin)?\b[^\r\n]*?\s-p(?<v>[^\s]+)", OptI),
        D("cli_secret_flag", @"\bsshpass\s+-p\s*" + Val, OptI),
        D("cli_secret_flag", @"\bConvertTo-SecureString\s+(-String\s+)?" + Val, OptI),
        D("cli_secret_flag", @"\bnet\s+user\s+(""[^""]+""|\S+)\s+(?<v>(?![/*])\S+)", OptI),
        D("cli_secret_flag", @"\bnet\s+use\s+[^\r\n]*?\s(?<v>(?![/*])[^\s/]+)\s+/user:", OptI),
        // FortiGate / Cisco / generic network CLI
        D("device_secret",
          @"\bset\s+(password|passwd|psksecret|secret|passphrase|sae-password|auth-pwd|priv-pwd|key|pre-shared-key)\s+(ENC\s+)?" + Val, OptI),
        D("device_secret", @"\benable\s+(secret|password)(\s+[0-9])?\s+" + Val, OptI),
        D("device_secret", @"\busername\s+\S+([ \t]+\S+)*?[ \t]+(password|secret)([ \t]+[0-9])?[ \t]+" + Val, OptI),
        D("device_secret", @"\bsnmp-server\s+community\s+" + Val, OptI),
        D("device_secret", @"(?m)^\s*(password|secret)(\s+[0-7])?\s+" + Val, OptI),

        // --- PII ---------------------------------------------------------------------------
        D("credit_card", @"(?<![\d-])(?:\d[ -]?){12,18}\d(?![\d-])", Opt, IsLuhnCard),
        D("ssn", @"\b(?!000|666|9\d\d)\d{3}-(?!00)\d{2}-(?!0000)\d{4}\b"),
    };

    // Token splitter for the entropy and password-shape passes.
    // Existing masks are matched first and skipped, so redaction is idempotent.
    private static readonly Regex TokenPattern = new(@"\[REDACTED:[a-z_]+\]|[^\s""'`,;()<>\[\]{}|]+", Opt, Timeout);
    private static readonly Regex AlreadyMasked = new(@"^\[REDACTED:[a-z_]+\]$", Opt, Timeout);

    private readonly ISet<string> _safeTerms;

    /// <param name="safeTerms">
    /// Exact tokens never to mask, compared case-insensitively — typically ITDocs hostnames
    /// (STR-DC01, J-SQL2), which are valuable client evidence but are password-shaped.
    /// </param>
    public SecretRedactor(IEnumerable<string>? safeTerms = null)
    {
        _safeTerms = new HashSet<string>(safeTerms ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
    }

    public RedactionResult Redact(string? text, RedactionMode mode = RedactionMode.General)
    {
        if (string.IsNullOrEmpty(text)) return new RedactionResult("", Array.Empty<string>());
        try
        {
            return RedactCore(text, mode);
        }
        catch (Exception)
        {
            // Fail closed. Never return the input when the filter could not finish.
            return new RedactionResult(FilterErrorMask, new[] { "filter_error" });
        }
    }

    private RedactionResult RedactCore(string text, RedactionMode mode)
    {
        // Order = detector priority: on equal start, the earlier detector names the span.
        var spans = new List<(int Start, int End, string Kind, int Order)>();

        foreach (var d in Detectors)
        {
            foreach (Match m in d.Pattern.Matches(text))
            {
                var g = m.Groups["v"];
                var (start, len) = g.Success ? (g.Index, g.Length) : (m.Index, m.Length);
                if (len == 0) continue;
                if (d.Validate != null && !d.Validate(text.Substring(start, len))) continue;
                spans.Add((start, start + len, d.Kind, spans.Count));
            }
        }

        foreach (Match m in TokenPattern.Matches(text))
        {
            var token = m.Value;
            if (IsMask(token)) continue;
            if (TokenShape.IsHighEntropySecret(token, _safeTerms))
                spans.Add((m.Index, m.Index + m.Length, "high_entropy", int.MaxValue - 1));
            else if (mode == RedactionMode.Keystrokes && TokenShape.IsPasswordShaped(token, _safeTerms))
                spans.Add((m.Index, m.Index + m.Length, "password_shape", int.MaxValue));
        }

        if (spans.Count == 0) return new RedactionResult(text, Array.Empty<string>());

        // Merge overlapping spans: earliest start wins; on a tie, the higher-priority detector names it.
        spans.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Order.CompareTo(b.Order));
        var merged = new List<(int Start, int End, string Kind)>();
        foreach (var (sStart, sEnd, sKind, _) in spans)
        {
            var s = (Start: sStart, End: sEnd, Kind: sKind);
            if (merged.Count > 0 && s.Start < merged[^1].End)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.End, s.End), last.Kind);
            }
            else merged.Add(s);
        }

        var sb = new StringBuilder(text.Length);
        var kinds = new List<string>();
        int pos = 0;
        foreach (var (start, end, kind) in merged)
        {
            sb.Append(text, pos, start - pos).Append(Mask(kind));
            kinds.Add(kind);
            pos = end;
        }
        sb.Append(text, pos, text.Length - pos);
        return new RedactionResult(sb.ToString(), kinds);
    }

    public static bool IsMask(string token) => AlreadyMasked.IsMatch(token);

    /// <summary>Luhn checksum over the digits of a 13-19 digit candidate.</summary>
    public static bool IsLuhnCard(string candidate)
    {
        var digits = candidate.Where(char.IsDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19) return false;
        if (digits.All(d => d == digits[0])) return false;   // 0000 0000 0000 0000 etc.

        int sum = 0;
        bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i];
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            dbl = !dbl;
        }
        return sum % 10 == 0;
    }
}
