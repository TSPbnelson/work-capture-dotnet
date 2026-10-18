using System.Text.RegularExpressions;
using WorkCapture.Privacy.Redaction;

namespace WorkCapture.Privacy.Keystrokes;

/// <summary>
/// The one entry point from the keyboard hook to storage. Raw characters go in; only
/// <see cref="SanitizedEntry"/> objects come out, and only those may be written to SQLite.
///
/// Three layers, each able only to REMOVE information:
///   1. <see cref="ContextGate"/>  — before buffering: password fields, credential prompts,
///      password managers, sign-in pages and remote sessions get counts only, no characters.
///   2. Credential-context rules — on each finished line, using what came before it:
///        A. the line after a command that prompts for a secret (ssh, sudo, runas, mysql -p...)
///        B. a one-word line submitted with Enter right after a field left with Tab (login form)
///        C. a standalone one-word line that is not plainly a word or command
///      Any of these masks the whole line.
///   3. <see cref="ITextRedactor"/> — pattern, entropy and password-shape masking of what is left.
///
/// FAIL-CLOSED: if anything throws, the buffer is discarded and a filter_error entry is emitted
/// in place of the text. Key counts survive every path, so time is never lost to privacy.
/// Not thread-safe: the hook adapter must call it from a single queue-draining thread.
/// </summary>
public sealed class KeystrokeSanitizer
{
    /// <summary>A gap this long between keys closes the current line.</summary>
    public static readonly TimeSpan IdleFlushAfter = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan RxTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly Regex SecretPromptingCommand = new(
        @"^\s*(sudo|su|ssh|scp|sftp|sshpass|runas|kinit|passwd|telnet|ftp|mysql|psql|smbclient|htpasswd|"
        + @"openssl|gpg|xfreerdp|rdesktop|(docker|podman|helm|az|oc)\s+login|git\s+(push|pull|clone|fetch)|"
        + @"net\s+use|enter-pssession|new-pssession|get-credential|connect-\w+)\b"
        + @"|-credential\b|--ask-(become-)?pass\b|\s-p\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);

    // Answers to "continue connecting (yes/no)?" and similar: kept, and they don't use up the
    // prompted-secret window (that is exactly how the first-connect ssh password leaked, 2026-10-18).
    private static readonly Regex ConfirmAnswer = new(@"^(y|n|yes|no)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RxTimeout);

    private static readonly Regex SlashCommand = new(@"^[a-z]{3,}(/[a-z?]{1,10})+$", RegexOptions.CultureInvariant, RxTimeout);

    private readonly ContextGate _gate;
    private readonly ITextRedactor _redactor;
    private readonly ISet<string> _safeTerms;
    private readonly KeystrokeLineBuffer _buffer = new();

    private InputContext? _ctx;
    private ContextGate.Decision _decision;
    private DateTime? _lastKeyAt;

    // Suppressed run (counts-only keys since the last emit)
    private int _suppressedRun;
    private DateTime _suppressedStart, _suppressedEnd;

    // Credential-context state, reset on every window change
    private int _maskNextLines;        // prompted-secret window: one-word lines still to mask
    private bool _promptFirstPending;  // the first real answer after the command is masked whatever its shape
    private Terminator? _previousTerminator;

    public long TotalKeys { get; private set; }
    public long SuppressedKeys { get; private set; }
    public long FilterErrors { get; private set; }

    public KeystrokeSanitizer(ContextGate? gate = null, ITextRedactor? redactor = null, IEnumerable<string>? safeTerms = null)
    {
        var terms = safeTerms?.ToArray() ?? Array.Empty<string>();
        _gate = gate ?? new ContextGate();
        _redactor = redactor ?? new SecretRedactor(terms);
        _safeTerms = new HashSet<string>(terms, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<SanitizedEntry> OnKey(KeystrokeEvent e, InputContext ctx)
    {
        var output = new List<SanitizedEntry>(2);
        TotalKeys++;
        try
        {
            if (_lastKeyAt is { } last && e.Timestamp - last >= IdleFlushAfter)
                EmitAll(Terminator.Idle, output);
            _lastKeyAt = e.Timestamp;

            var decision = _gate.Evaluate(ctx);

            if (_ctx is null || ctx.WindowKey != _ctx.WindowKey)
            {
                EmitAll(Terminator.WindowChange, output);
                _maskNextLines = 0;
                _promptFirstPending = false;
                _previousTerminator = null;
            }
            else if (decision != _decision)
            {
                // e.g. focus moved from the username box into the password box
                EmitAll(Terminator.ContextChange, output);
            }

            _ctx = ctx;
            _decision = decision;

            if (!decision.AllowsText)
            {
                SuppressedKeys++;
                if (_suppressedRun == 0) _suppressedStart = e.Timestamp;
                _suppressedEnd = e.Timestamp;
                _suppressedRun++;
                if (e.Kind == KeyKind.Enter) EmitSuppressed(Terminator.Enter, output);
                return output;
            }

            if (_buffer.Add(e) is { } line)
                output.Add(Sanitize(line, ctx));
        }
        catch (Exception)
        {
            output.Add(FailClosed(e.Timestamp));
        }
        return output;
    }

    /// <summary>Close everything pending: call on idle timer, session lock/disconnect, shutdown.</summary>
    public IReadOnlyList<SanitizedEntry> Flush(Terminator reason)
    {
        var output = new List<SanitizedEntry>(2);
        try { EmitAll(reason, output); }
        catch (Exception) { output.Add(FailClosed(_lastKeyAt ?? DateTime.Now)); }
        return output;
    }

    private void EmitAll(Terminator t, List<SanitizedEntry> output)
    {
        EmitSuppressed(t, output);
        if (_ctx != null && _buffer.Flush(t) is { } line)
            output.Add(Sanitize(line, _ctx));
    }

    private void EmitSuppressed(Terminator t, List<SanitizedEntry> output)
    {
        if (_suppressedRun == 0 || _ctx is null) return;
        output.Add(new SanitizedEntry(_suppressedStart, _suppressedEnd, _ctx.ProcessName, _ctx.WindowTitle,
            "", t, Array.Empty<string>(), Suppressed: true, _decision.Reason, _suppressedRun, Array.Empty<string>()));
        _suppressedRun = 0;
        _previousTerminator = t;
    }

    private SanitizedEntry Sanitize(RawLine line, InputContext ctx)
    {
        string text;
        IReadOnlyList<string> kinds;
        var trimmed = line.Text.Trim();
        bool singleToken = trimmed.Length > 0 && !trimmed.Any(char.IsWhiteSpace);
        bool submitted = line.Terminator is Terminator.Enter or Terminator.Tab;

        bool inPromptWindow = _maskNextLines > 0 && line.Terminator == Terminator.Enter && trimmed.Length > 0;
        if (inPromptWindow && !ConfirmAnswer.IsMatch(trimmed) && (_promptFirstPending || singleToken))
        {
            // After ssh/sudo/runas...: the first non-yes/no answer, then any further one-word lines
            // (a retried password) up to the window size. A multi-word line ends the window below.
            _maskNextLines--;
            _promptFirstPending = false;
            (text, kinds) = Whole("prompted_secret");
        }
        else if (inPromptWindow && ConfirmAnswer.IsMatch(trimmed))
        {
            (text, kinds) = (line.Text, Array.Empty<string>());   // yes/no: keep, window stays open
        }
        else if (_previousTerminator == Terminator.Tab && line.Terminator == Terminator.Enter && singleToken)
        {
            (text, kinds) = Whole("form_secret");
        }
        else if (singleToken && submitted && LooksLikeStandaloneSecret(trimmed))
        {
            (text, kinds) = Whole("standalone_secret");
        }
        else
        {
            if (inPromptWindow) { _maskNextLines = 0; _promptFirstPending = false; }   // a real command: window over
            var r = _redactor.Redact(line.Text, RedactionMode.Keystrokes);
            (text, kinds) = (r.Text, r.Kinds);
            if (line.Terminator == Terminator.Enter && SecretPromptingCommand.IsMatch(line.Text))
            {
                _maskNextLines = 3;
                _promptFirstPending = true;
            }
        }

        _previousTerminator = line.Terminator;
        return new SanitizedEntry(line.Start, line.End, ctx.ProcessName, ctx.WindowTitle, text,
            line.Terminator, kinds, Suppressed: false, null, line.KeyCount, line.Shortcuts);
    }

    private bool LooksLikeStandaloneSecret(string token) =>
        token.Length >= 6
        && TokenShape.CharClassCount(token) >= 2
        && !TokenShape.IsSafeShape(token, _safeTerms)
        && !SlashCommand.IsMatch(token);

    private static (string, IReadOnlyList<string>) Whole(string kind) => (SecretRedactor.Mask(kind), new[] { kind });

    private SanitizedEntry FailClosed(DateTime at)
    {
        FilterErrors++;
        _buffer.Reset();
        _suppressedRun = 0;
        _maskNextLines = 0;
        return new SanitizedEntry(at, at, _ctx?.ProcessName ?? "", _ctx?.WindowTitle ?? "",
            SecretRedactor.FilterErrorMask, Terminator.ContextChange, new[] { "filter_error" },
            Suppressed: true, "filter_error", 0, Array.Empty<string>());
    }
}
