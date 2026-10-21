using System.Text.RegularExpressions;

namespace WorkCapture.Privacy.Keystrokes;

/// <summary>
/// Layer 1: decides BEFORE a character is buffered whether text may be recorded at all for the
/// current input target. When it says no, only the key count is kept, so the time still bills.
///
/// This gate is intentionally broader than the screenshot privacy filter. Over-blocking a
/// screenshot loses billing evidence; over-blocking typed text only loses narrative detail,
/// because the activity counts (and therefore the minutes) are recorded either way.
/// </summary>
public sealed class ContextGate
{
    public enum Mode { Text, CountsOnly }

    public readonly record struct Decision(Mode Mode, string Reason)
    {
        public bool AllowsText => Mode == Mode.Text;
    }

    public sealed class Policy
    {
        /// <summary>Process names without .exe, case-insensitive.</summary>
        public ISet<string> BlockedProcesses { get; init; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // password managers
            "1password", "bitwarden", "keeper", "keeperpasswordmanager", "keepass", "keepassxc",
            "lastpass", "dashlane", "roboform",
            // Windows credential / elevation / logon surfaces
            "credentialuibroker", "consent", "logonui", "lsass", "winlogon", "lockapp",
            "credentialenrollmentmanager", "microsoft.aad.brokerplugin",
        };

        public IReadOnlyList<Regex> BlockedTitlePatterns { get; init; } = new[]
        {
            Rx(@"pass(word|phrase|code)?|\bpwd\b|credential|secret|private key|recovery key|bitlocker"),
            Rx(@"sign[\s-]?in|log[\s-]?[io]n|logon|authenticat|verify (it's )?you|unlock"),
            Rx(@"\b(mfa|2fa|otp|one[\s-]time|verification code|security code|authenticator)\b"),
            Rx(@"vault|keeper|bitwarden|1password|lastpass|keepass"),
            Rx(@"\bpin\b"),
        };

        public IReadOnlyList<Regex> BlockedUrlPatterns { get; init; } = new[]
        {
            Rx(@"login\.microsoftonline\.com|login\.live\.com|accounts\.google\.com|/oauth2?/|/saml|/sso\b|/login|/signin|/auth"),
            Rx(@"vault\.|bitwarden\.com|1password\.com|keepersecurity\.|lastpass\.com"),
        };

        /// <summary>
        /// RDP / ScreenConnect / console windows. The local machine cannot see whether the
        /// REMOTE field has focus on a password box (UIA only sees the client window), so by
        /// default nothing typed into a remote session is kept as text.
        /// </summary>
        public bool AllowTextInRemoteSessions { get; init; } = false;

        private static Regex Rx(string p) => new(p,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    private readonly Policy _policy;

    public ContextGate(Policy? policy = null) => _policy = policy ?? new Policy();

    public Decision Evaluate(InputContext? ctx)
    {
        try
        {
            if (ctx is null) return CountsOnly("no_context");
            if (ctx.IsSecureDesktop) return CountsOnly("secure_desktop");
            if (ctx.FocusIsPassword is null) return CountsOnly("focus_unknown");
            if (ctx.FocusIsPassword == true) return CountsOnly("password_field");
            if (_policy.BlockedProcesses.Contains(ctx.ProcessName ?? "")) return CountsOnly("blocked_process");
            if (_policy.BlockedTitlePatterns.Any(r => r.IsMatch(ctx.WindowTitle ?? ""))) return CountsOnly("blocked_title");
            if (ctx.Url != null && _policy.BlockedUrlPatterns.Any(r => r.IsMatch(ctx.Url))) return CountsOnly("blocked_url");
            if (ctx.IsRemoteSession && !_policy.AllowTextInRemoteSessions) return CountsOnly("remote_session");
            return new Decision(Mode.Text, "");
        }
        catch (Exception)
        {
            return CountsOnly("gate_error");   // fail closed
        }
    }

    private static Decision CountsOnly(string reason) => new(Mode.CountsOnly, reason);
}
