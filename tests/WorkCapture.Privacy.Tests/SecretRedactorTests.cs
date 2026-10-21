using WorkCapture.Privacy.Redaction;
using Xunit;

namespace WorkCapture.Privacy.Tests;

/// <summary>
/// Vendor tokens are assembled at runtime (prefix + filler) so this file never contains a
/// literal that GitHub push protection or gitleaks would flag on the repo itself.
/// </summary>
public class SecretRedactorTests
{
    private readonly SecretRedactor _r = new(safeTerms: new[] { "STR-DC01", "J-SQL2" });

    private static string Fill(int n, string alphabet = "aB3dE5gH7jK9mN1pQ2sT4vW6yZ8")
        => new(Enumerable.Range(0, n).Select(i => alphabet[(i * 7 + 3) % alphabet.Length]).ToArray());

    private RedactionResult G(string s) => _r.Redact(s, RedactionMode.General);
    private RedactionResult K(string s) => _r.Redact(s, RedactionMode.Keystrokes);

    private static void AssertMasked(RedactionResult r, string secret, string kind)
    {
        Assert.DoesNotContain(secret, r.Text);
        Assert.Contains($"[REDACTED:{kind}]", r.Text);
        Assert.Contains(kind, r.Kinds);
    }

    // ---- null / empty / clean -----------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_input_returns_empty(string? input)
    {
        var r = _r.Redact(input, RedactionMode.Keystrokes);
        Assert.Equal("", r.Text);
        Assert.False(r.WasRedacted);
    }

    [Theory]
    [InlineData("Restart the print spooler on the file server")]
    [InlineData("Get-ADUser -Identity owen -Properties LastLogonDate")]
    [InlineData("ping 10.100.15.21")]
    [InlineData("ssh root@webhost-01.tier3msp.com")]
    [InlineData(@"robocopy C:\Data \\STR-FS01\Data /MIR")]
    [InlineData("tenant 72f988bf-86f1-41af-91ab-2d7cd011db47 sign-in logs")]
    [InlineData("upgrade to 7.4.3 then reboot")]
    [InlineData(@"whoami returned CORP\bnelson")]
    [InlineData("mail owen@easyitguys.com about the backup")]
    public void Ordinary_work_text_is_untouched_in_keystroke_mode(string input)
    {
        var r = K(input);
        Assert.Equal(input, r.Text);
        Assert.False(r.WasRedacted);
    }

    [Fact]
    public void Known_hostname_safe_term_survives_password_shape()
    {
        Assert.Equal("Enter-PSSession STR-DC01", K("Enter-PSSession STR-DC01").Text);
        // ...but an unknown hostname-looking token with 3 classes is masked (fail-safe).
        Assert.Contains("[REDACTED:password_shape]", K("Enter-PSSession ABC-XY99").Text);
    }

    // ---- structured secrets ---------------------------------------------------------------

    [Fact]
    public void Private_key_block_is_masked_whole()
    {
        var body = Fill(64);
        var pem = $"-----BEGIN OPENSSH PRIVATE KEY-----\n{body}\n-----END OPENSSH PRIVATE KEY-----";
        var r = G("key: " + pem + " done");
        AssertMasked(r, body, "private_key");
        Assert.EndsWith(" done", r.Text);
    }

    [Fact]
    public void Url_credentials_mask_only_the_password()
    {
        var r = G("git clone https://bnelson:Sup3rS3cret!@git.example.com/repo.git");
        AssertMasked(r, "Sup3rS3cret!", "url_credentials");
        Assert.Contains("https://bnelson:", r.Text);
        Assert.Contains("@git.example.com/repo.git", r.Text);
    }

    [Fact]
    public void Jwt_is_masked()
    {
        var jwt = "eyJ" + Fill(20) + ".eyJ" + Fill(30) + "." + Fill(40);
        AssertMasked(G("token " + jwt), jwt, "jwt");
    }

    // ---- vendor tokens --------------------------------------------------------------------

    public static IEnumerable<object[]> VendorTokens() => new[]
    {
        new object[] { "AK" + "IA" + "QWERTYUIOPASDFGH", "aws_access_key" },
        new object[] { "gh" + "p_" + Fill(36), "github_token" },
        new object[] { "github" + "_pat_" + Fill(40), "github_token" },
        new object[] { "xo" + "xb-" + "1234567890-" + Fill(24), "slack_token" },
        new object[] { "sk-" + "ant-" + "api03-" + Fill(80), "api_key" },
        new object[] { "sk" + "_live_" + Fill(24), "stripe_key" },
        new object[] { "AI" + "za" + Fill(35), "google_api_key" },
        new object[] { "Ab1" + "8Q~" + Fill(34, "abcdefXYZ0123_~.-"), "azure_client_secret" },
    };

    [Theory]
    [MemberData(nameof(VendorTokens))]
    public void Vendor_tokens_are_masked_in_general_mode(string token, string kind)
    {
        var r = G($"export VALUE {token} now");
        Assert.DoesNotContain(token, r.Text);
        Assert.Contains(kind, r.Kinds);
    }

    [Fact]
    public void Bearer_token_keeps_scheme_word()
    {
        var tok = Fill(40);
        var r = G("curl -H \"Authorization: Bearer " + tok + "\"");
        Assert.DoesNotContain(tok, r.Text);
        Assert.Contains("Bearer", r.Text);
    }

    // ---- labelled values ------------------------------------------------------------------

    [Theory]
    [InlineData("password=hunter2", "hunter2")]
    [InlineData("Password: hunter2", "hunter2")]
    [InlineData("pwd = 'hunter2'", "hunter2")]
    [InlineData("\"client_secret\": \"abc.def\"", "abc.def")]
    [InlineData("Server=sql1;User Id=sa;Password=Pa$$w0rd;", "Pa$$w0rd")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=x;AccountKey=abc123==;", "abc123==")]
    [InlineData("snmp community=publicRO", "publicRO")]
    [InlineData("Authorization: Bearer abcdefghijklmnop", "abcdefghijklmnop")]
    public void Labelled_secret_values_are_masked(string input, string secret)
    {
        var r = G(input);
        Assert.DoesNotContain(secret, r.Text);
        Assert.True(r.WasRedacted);
    }

    [Fact]
    public void Labelled_secret_keeps_label_readable()
    {
        Assert.Equal("password=[REDACTED:labelled_secret] next", G("password=hunter2 next").Text);
    }

    // ---- command-line forms ---------------------------------------------------------------

    [Theory]
    [InlineData("New-LocalUser bob -Password Winter2026", "Winter2026")]
    [InlineData("Set-ADAccountPassword bob -NewPassword hunter2 -Reset", "hunter2")]
    [InlineData("tool --password=hunter2 --verbose", "hunter2")]
    [InlineData("tool --api-key hunter2", "hunter2")]
    [InlineData("psexec \\\\srv /u admin /p:hunter2 cmd", "hunter2")]
    [InlineData("mysql -u root -phunter2 db", "hunter2")]
    [InlineData("sshpass -p hunter2 ssh root@host", "hunter2")]
    [InlineData("$s = ConvertTo-SecureString 'hunter2' -AsPlainText -Force", "hunter2")]
    [InlineData("net user bob hunter2 /add", "hunter2")]
    [InlineData("net use Z: \\\\srv\\share hunter2 /user:CORP\\bob", "hunter2")]
    [InlineData("set psksecret hunter2", "hunter2")]
    [InlineData("set password ENC hunter2", "hunter2")]
    [InlineData("enable secret 5 hunter2", "hunter2")]
    [InlineData("username admin privilege 15 secret hunter2", "hunter2")]
    [InlineData("snmp-server community hunter2 RO", "hunter2")]
    public void Cli_secrets_are_masked(string input, string secret)
    {
        var r = G(input);
        Assert.DoesNotContain(secret, r.Text);
        Assert.True(r.WasRedacted);
    }

    [Fact]
    public void Ssh_port_flag_is_not_treated_as_a_password()
    {
        Assert.Equal("ssh -p 2222 root@10.0.0.5", G("ssh -p 2222 root@10.0.0.5").Text);
    }

    [Fact]
    public void Net_user_with_star_prompt_is_not_masked()
    {
        Assert.Equal("net user bob * /domain", G("net user bob * /domain").Text);
    }

    // ---- PII ------------------------------------------------------------------------------

    [Theory]
    [InlineData("4111 1111 1111 1111")]
    [InlineData("4111-1111-1111-1111")]
    [InlineData("4111111111111111")]
    [InlineData("378282246310005")]      // Amex test number, 15 digits
    public void Luhn_valid_card_numbers_are_masked(string card)
    {
        AssertMasked(G($"card {card} exp 12/29"), card, "credit_card");
    }

    [Theory]
    [InlineData("4111 1111 1111 1112")]  // fails Luhn
    [InlineData("0000 0000 0000 0000")]  // repeated digit
    [InlineData("1234")]                  // too short
    public void Non_card_numbers_are_kept(string digits)
    {
        Assert.DoesNotContain("credit_card", G($"ref {digits}").Kinds);
    }

    [Fact]
    public void Ssn_is_masked_but_invalid_area_is_not()
    {
        AssertMasked(G("SSN 123-45-6789"), "123-45-6789", "ssn");
        Assert.DoesNotContain("ssn", G("ref 000-12-3456").Kinds);
    }

    // ---- entropy --------------------------------------------------------------------------

    [Fact]
    public void High_entropy_base64_is_masked()
    {
        var blob = Fill(40) + "+/";
        AssertMasked(G("value " + blob), blob, "high_entropy");
    }

    [Fact]
    public void Long_hex_key_is_masked_but_guid_is_not()
    {
        var hex = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";
        AssertMasked(G("key " + hex), hex, "high_entropy");
        Assert.False(G("id 9f86d081-884c-7d65-9a2f-eaa0c55ad015").WasRedacted);
    }

    [Theory]
    [InlineData("Microsoft.PowerShell.Management")]
    [InlineData("edgetech-fileserver-backup-01")]
    [InlineData("C:\\Windows\\System32\\drivers\\etc\\hosts")]
    public void Long_but_low_entropy_identifiers_are_kept(string token)
    {
        Assert.False(G(token).WasRedacted);
    }

    // ---- password shape (keystrokes only) ---------------------------------------------------

    [Theory]
    [InlineData("Summer2024!")]
    [InlineData("P@ssw0rd")]
    [InlineData("Winter.Is2Coming")]
    public void Password_shaped_tokens_are_masked_in_keystroke_mode_only(string pw)
    {
        AssertMasked(K($"try {pw} again"), pw, "password_shape");
        Assert.Equal($"try {pw} again", G($"try {pw} again").Text);
    }

    // ---- output properties ------------------------------------------------------------------

    [Fact]
    public void Mask_does_not_leak_secret_length()
    {
        var a = G("password=a1").Text;
        var b = G("password=" + new string('x', 60)).Text;
        Assert.Equal(a, b);
    }

    [Fact]
    public void Multiple_secrets_in_one_string_are_all_masked()
    {
        var r = G("password=one1 and token=two2 and card 4111111111111111");
        Assert.DoesNotContain("one1", r.Text);
        Assert.DoesNotContain("two2", r.Text);
        Assert.DoesNotContain("4111111111111111", r.Text);
        Assert.Equal(3, r.Kinds.Count);
    }

    [Fact]
    public void Redaction_is_idempotent()
    {
        var once = K("password=hunter2 and P@ssw0rd").Text;
        Assert.Equal(once, K(once).Text);
    }

    [Fact]
    public void Existing_masks_keep_their_label()
    {
        var s = "New-LocalUser bob -Password [REDACTED:password_shape] and [REDACTED:prompted_secret]";
        var r = K(s);
        Assert.Equal(s, r.Text);
        Assert.False(r.WasRedacted);
    }

    [Fact]
    public void Pathological_input_completes_or_fails_closed()
    {
        // 50k chars of near-miss structure: must finish quickly, and every labelled value is
        // either masked or the whole result is the fail-closed sentinel.
        var input = string.Concat(Enumerable.Repeat("password= a:b@ 4111 ", 2500));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = G(input);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.DoesNotContain("a:b@", r.Text);
    }
}
