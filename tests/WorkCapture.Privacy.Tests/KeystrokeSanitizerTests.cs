using WorkCapture.Privacy.Keystrokes;
using WorkCapture.Privacy.Redaction;
using Xunit;

namespace WorkCapture.Privacy.Tests;

public class KeystrokeSanitizerTests
{
    private static readonly DateTime T0 = new(2026, 10, 17, 9, 0, 0);

    private static readonly InputContext Notepad = new("notepad", "notes.txt - Notepad");
    private static readonly InputContext Terminal = new("WindowsTerminal", "Windows PowerShell", IsTerminal: true);
    private static readonly InputContext Rdp = new("mstsc", "STR-DC01 - Remote Desktop Connection", IsRemoteSession: true);

    private DateTime _clock = T0;
    private DateTime Tick() => _clock = _clock.AddMilliseconds(120);

    /// <summary>Type a string; '\n' = Enter, '\t' = Tab, '\b' = Backspace.</summary>
    private List<SanitizedEntry> Type(KeystrokeSanitizer s, InputContext ctx, string keys)
    {
        var output = new List<SanitizedEntry>();
        foreach (var c in keys)
        {
            var e = c switch
            {
                '\n' => KeystrokeEvent.Key(KeyKind.Enter, Tick()),
                '\t' => KeystrokeEvent.Key(KeyKind.Tab, Tick()),
                '\b' => KeystrokeEvent.Key(KeyKind.Backspace, Tick()),
                _ => KeystrokeEvent.Of(c, Tick()),
            };
            output.AddRange(s.OnKey(e, ctx));
        }
        return output;
    }

    private static string AllText(IEnumerable<SanitizedEntry> entries) => string.Join(" | ", entries.Select(e => e.Text));

    // ---- reconstruction ---------------------------------------------------------------------

    [Fact]
    public void Ordinary_command_is_kept()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Terminal, "Get-Service spooler\n");
        Assert.Single(e);
        Assert.Equal("Get-Service spooler", e[0].Text);
        Assert.Equal(Terminator.Enter, e[0].Terminator);
        Assert.Equal(20, e[0].KeyCount);
        Assert.Empty(e[0].Redactions);
    }

    [Fact]
    public void Backspace_edits_are_applied()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Notepad, "restsrt\b\b\bart the server\n");
        Assert.Equal("restart the server", e.Single().Text);
    }

    [Fact]
    public void Ctrl_backspace_deletes_the_last_word()
    {
        var s = new KeystrokeSanitizer();
        Type(s, Notepad, "reboot wrongword");
        var e = s.OnKey(KeystrokeEvent.Key(KeyKind.Backspace, Tick(), KeyModifiers.Ctrl), Notepad).ToList();
        e.AddRange(Type(s, Notepad, "tonight\n"));
        Assert.Equal("reboot tonight", e.Single().Text);
    }

    [Fact]
    public void Shortcuts_are_recorded_by_name_and_never_enter_text()
    {
        var s = new KeystrokeSanitizer();
        Type(s, Notepad, "note ");
        s.OnKey(KeystrokeEvent.Shortcut(KeyModifiers.Ctrl, "V", Tick()), Notepad);
        var e = Type(s, Notepad, "done\n").Single();
        Assert.Equal("note done", e.Text);
        Assert.Equal(new[] { "Ctrl+V" }, e.Shortcuts);
    }

    [Fact]
    public void AltGr_character_is_text_not_a_shortcut()
    {
        var s = new KeystrokeSanitizer();
        Type(s, Notepad, "owen");
        s.OnKey(new KeystrokeEvent(KeyKind.Character, '@', KeyModifiers.Ctrl | KeyModifiers.Alt, "Q", Tick()), Notepad);
        var e = Type(s, Notepad, "easyitguys.com\n").Single();
        Assert.Equal("owen@easyitguys.com", e.Text);
        Assert.Empty(e.Shortcuts);
    }

    [Fact]
    public void Navigation_marks_line_uncertain_but_keeps_text()
    {
        var s = new KeystrokeSanitizer();
        Type(s, Notepad, "abc");
        s.OnKey(KeystrokeEvent.Key(KeyKind.Navigation, Tick(), name: "Left"), Notepad);
        var e = Type(s, Notepad, " def\n").Single();
        Assert.Equal("abc def", e.Text);
    }

    [Fact]
    public void Long_line_flushes_at_overflow()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Notepad, string.Concat(Enumerable.Repeat("word ", 60)));
        Assert.Contains(e, x => x.Terminator == Terminator.Overflow);
        Assert.All(e, x => Assert.True(x.Text.Length <= 256));
    }

    // ---- layer 1: context gate ----------------------------------------------------------------

    [Fact]
    public void Password_field_keeps_counts_but_no_text()
    {
        var s = new KeystrokeSanitizer();
        var pwField = Notepad with { WindowTitle = "Admin Center", FocusIsPassword = true };
        var e = Type(s, pwField, "Hunter2hunter\n");
        var entry = Assert.Single(e);
        Assert.True(entry.Suppressed);
        Assert.Equal("", entry.Text);
        Assert.Equal("password_field", entry.SuppressReason);
        Assert.Equal(14, entry.KeyCount);
        Assert.Equal(14, s.SuppressedKeys);
    }

    [Fact]
    public void Unknown_focus_fails_closed()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Notepad with { FocusIsPassword = null }, "anything\n");
        Assert.Equal("focus_unknown", Assert.Single(e).SuppressReason);
    }

    [Theory]
    [InlineData("1password", "1Password", null, "blocked_process")]
    [InlineData("CredentialUIBroker", "Windows Security", null, "blocked_process")]
    [InlineData("msedge", "Sign in to your account - Microsoft Edge", null, "blocked_title")]
    [InlineData("chrome", "Portal - Google Chrome", "https://login.microsoftonline.com/common/oauth2", "blocked_url")]
    [InlineData("chrome", "Enter verification code", null, "blocked_title")]
    public void Sensitive_targets_get_counts_only(string proc, string title, string? url, string reason)
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, new InputContext(proc, title, url), "secretvalue\n");
        var entry = Assert.Single(e);
        Assert.True(entry.Suppressed);
        Assert.Equal(reason, entry.SuppressReason);
        Assert.Equal("", entry.Text);
    }

    [Fact]
    public void Secure_desktop_gets_counts_only()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Notepad with { IsSecureDesktop = true }, "x\n");
        Assert.Equal("secure_desktop", Assert.Single(e).SuppressReason);
    }

    [Fact]
    public void Remote_session_is_counts_only_by_default()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Rdp, "administrator\tWinter2026!\n");
        Assert.All(e, x => Assert.True(x.Suppressed));
        Assert.DoesNotContain("Winter", AllText(e));
        Assert.DoesNotContain("administrator", AllText(e));
        Assert.Equal(26, e.Sum(x => x.KeyCount));
    }

    [Fact]
    public void Remote_session_text_when_enabled_is_still_redacted()
    {
        var gate = new ContextGate(new ContextGate.Policy { AllowTextInRemoteSessions = true });
        var s = new KeystrokeSanitizer(gate);
        var e = Type(s, Rdp, "administrator\tWinter2026!\nGet-ADUser owen\n");
        var text = AllText(e);
        Assert.DoesNotContain("Winter2026!", text);
        Assert.Contains("Get-ADUser owen", text);
    }

    [Fact]
    public void Focus_moving_into_a_password_field_flushes_first()
    {
        var s = new KeystrokeSanitizer();
        var browser = new InputContext("msedge", "Contoso Admin - Edge");
        var e = Type(s, browser, "bnelson");
        e.AddRange(Type(s, browser with { FocusIsPassword = true }, "Hunter2hunter\n"));
        Assert.Equal(2, e.Count);
        Assert.Equal("bnelson", e[0].Text);
        Assert.Equal(Terminator.ContextChange, e[0].Terminator);
        Assert.True(e[1].Suppressed);
        Assert.DoesNotContain("Hunter2", AllText(e));
    }

    [Fact]
    public void Window_change_flushes_under_the_old_window()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Notepad, "first note");
        e.AddRange(Type(s, Terminal, "x"));
        var first = Assert.Single(e);
        Assert.Equal("first note", first.Text);
        Assert.Equal("notepad", first.ProcessName);
        Assert.Equal(Terminator.WindowChange, first.Terminator);
    }

    // ---- layer 2: credential context -------------------------------------------------------

    [Theory]
    [InlineData("ssh root@10.100.15.21\n")]
    [InlineData("sudo systemctl restart nginx\n")]
    [InlineData("runas /user:CORP\\admin cmd\n")]
    [InlineData("mysql -u root -p\n")]
    [InlineData("Enter-PSSession J-SQL2 -Credential CORP\\admin\n")]
    [InlineData("git push origin main\n")]
    public void Line_after_a_secret_prompting_command_is_masked(string command)
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Terminal, command + "correct horse battery staple\n");
        Assert.Equal(2, e.Count);
        Assert.Equal(command.TrimEnd('\n').Replace("\\", "\\"), e[0].Text);
        Assert.Equal("[REDACTED:prompted_secret]", e[1].Text);
    }

    [Fact]
    public void Only_one_line_is_masked_after_a_prompting_command()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Terminal, "ssh root@host\npassphrase here\nuptime -p\n");
        Assert.Equal("uptime -p", e[2].Text);
    }

    [Fact]
    public void Tab_then_enter_single_word_is_masked_even_if_lowercase()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, new InputContext("someapp", "Connect"), "administrator\tcorrecthorsebattery\n");
        Assert.Equal("administrator", e[0].Text);
        Assert.Equal("[REDACTED:form_secret]", e[1].Text);
    }

    [Theory]
    [InlineData("hunter2")]
    [InlineData("Tr0ub4dor")]
    [InlineData("s3cr3t-value")]
    public void Standalone_single_token_line_is_masked(string pw)
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Terminal, pw + "\n").Single();
        Assert.Equal("[REDACTED:standalone_secret]", e.Text);
    }

    [Theory]
    [InlineData("ipconfig")]
    [InlineData("exit")]
    [InlineData("ipconfig/all")]
    [InlineData("gpupdate/force")]
    [InlineData("Get-ChildItem")]
    [InlineData("10.100.15.21")]
    [InlineData("J-SQL2")]
    public void Standalone_commands_and_known_hosts_are_kept(string line)
    {
        var s = new KeystrokeSanitizer(safeTerms: new[] { "J-SQL2" });
        Assert.Equal(line, Type(s, Terminal, line + "\n").Single().Text);
    }

    [Fact]
    public void Credential_state_resets_on_window_change()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Terminal, "ssh root@host\n");
        e.AddRange(Type(s, Notepad, "meeting notes for owen\n"));
        Assert.Equal("meeting notes for owen", e.Last().Text);
    }

    [Fact]
    public void Title_change_in_the_same_window_keeps_the_line_and_the_ssh_mask()
    {
        var s = new KeystrokeSanitizer();
        var before = new InputContext("WindowsTerminal", "PowerShell", IsTerminal: true, WindowHandle: 0x1234);
        var after = before with { WindowTitle = "ssh root@10.100.15.21" };
        var e = Type(s, before, "ssh root@10.100.15.21\n");
        e.AddRange(Type(s, after, "Tr0ub4dor&3 plus words\n"));
        Assert.Equal(2, e.Count);
        Assert.Equal("[REDACTED:prompted_secret]", e[1].Text);
    }

    [Fact]
    public void Notepad_dirty_marker_does_not_split_the_line()
    {
        var s = new KeystrokeSanitizer();
        var clean = new InputContext("notepad", "notes.txt - Notepad", WindowHandle: 0x99);
        var e = Type(s, clean, "h");
        e.AddRange(Type(s, clean with { WindowTitle = "*notes.txt - Notepad" }, "ello world\n"));
        Assert.Equal("hello world", Assert.Single(e).Text);
    }

    // ---- layer 3: pattern redaction inside lines ------------------------------------------

    [Fact]
    public void Inline_secrets_in_a_command_are_masked()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Terminal, "New-LocalUser bob -Password Summer2026! -FullName Bob\n").Single();
        Assert.DoesNotContain("Summer2026", e.Text);
        Assert.Contains("New-LocalUser bob -Password", e.Text);
        Assert.Contains("-FullName Bob", e.Text);
    }

    [Fact]
    public void Card_number_typed_in_notes_is_masked()
    {
        var s = new KeystrokeSanitizer();
        var e = Type(s, Notepad, "card 4111 1111 1111 1111 ok\n").Single();
        Assert.Equal("card [REDACTED:credit_card] ok", e.Text);
    }

    // ---- flushing and fail-closed --------------------------------------------------------

    [Fact]
    public void Idle_gap_closes_the_line()
    {
        var s = new KeystrokeSanitizer();
        Type(s, Notepad, "before");
        _clock = _clock.Add(KeystrokeSanitizer.IdleFlushAfter);
        var e = Type(s, Notepad, "a");
        Assert.Equal(Terminator.Idle, Assert.Single(e).Terminator);
        Assert.Equal("before", e[0].Text);
    }

    [Fact]
    public void Explicit_flush_emits_pending_text_and_suppressed_counts()
    {
        var s = new KeystrokeSanitizer();
        Type(s, Notepad, "pending");
        var e = s.Flush(Terminator.Shutdown);
        Assert.Equal("pending", Assert.Single(e).Text);
        Assert.Empty(s.Flush(Terminator.Shutdown));
    }

    private sealed class ThrowingRedactor : ITextRedactor
    {
        public RedactionResult Redact(string? text, RedactionMode mode) => throw new InvalidOperationException("boom");
    }

    [Fact]
    public void Redactor_failure_fails_closed_without_raw_text()
    {
        var s = new KeystrokeSanitizer(redactor: new ThrowingRedactor());
        var e = Type(s, Notepad, "the quick brown fox\n");
        var entry = Assert.Single(e);
        Assert.Equal(SecretRedactor.FilterErrorMask, entry.Text);
        Assert.Contains("filter_error", entry.Redactions);
        Assert.Equal(1, s.FilterErrors);
        // ...and the sanitizer keeps working afterwards with a clean buffer.
        Assert.Equal(SecretRedactor.FilterErrorMask, Type(s, Notepad, "again\n").Single().Text);
    }

    [Fact]
    public void Every_key_is_counted_whatever_the_path()
    {
        var s = new KeystrokeSanitizer();
        Type(s, Notepad, "abc\n");
        Type(s, Rdp, "def\n");
        Type(s, Notepad with { FocusIsPassword = true }, "ghi\n");
        Assert.Equal(12, s.TotalKeys);
        Assert.Equal(8, s.SuppressedKeys);
    }

    [Fact]
    public void No_entry_ever_contains_a_typed_password_end_to_end()
    {
        // A realistic morning: notes, a terminal login, a web sign-in, an RDP login, a config edit.
        var s = new KeystrokeSanitizer(safeTerms: new[] { "STR-DC01" });
        var all = new List<SanitizedEntry>();
        all.AddRange(Type(s, Notepad, "Straus DHCP review\n"));
        all.AddRange(Type(s, Terminal, "ssh admin@192.168.44.1\nFgt!Adm1n#44\nshow system interface\n"));
        all.AddRange(Type(s, new InputContext("msedge", "Sign in - Microsoft Edge"), "brad@tier3msp.com\tEdge$Pass99\n"));
        all.AddRange(Type(s, Rdp, "Administrator\tRdpP@ss2026\n"));
        all.AddRange(Type(s, Terminal, "config system admin\nset password N3wAdm!nPw\nend\n"));
        all.AddRange(s.Flush(Terminator.Shutdown));

        var text = AllText(all);
        foreach (var pw in new[] { "Fgt!Adm1n#44", "Edge$Pass99", "RdpP@ss2026", "N3wAdm!nPw" })
            Assert.DoesNotContain(pw, text);
        Assert.Contains("Straus DHCP review", text);
        Assert.Contains("ssh admin@192.168.44.1", text);
        Assert.Contains("show system interface", text);
    }
}
