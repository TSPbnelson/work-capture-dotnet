using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WorkCapture.Config;

/// <summary>
/// Client detection rules configuration
/// </summary>
public class ClientRulesConfig
{
    public List<ClientRule> Clients { get; set; } = new();

    private static readonly string ConfigPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "config", "clients.json");

    public static ClientRulesConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                return JsonSerializer.Deserialize<ClientRulesConfig>(json, JsonOptions)
                    ?? GetDefaults();
            }
        }
        catch (Exception ex)
        {
            Logger.Warning($"Failed to load client rules: {ex.Message}");
        }

        return GetDefaults();
    }

    private static ClientRulesConfig GetDefaults() => new()
    {
        Clients = new List<ClientRule>
        {
            new()
            {
                Name = "JNJ Services",
                Code = "JNJ",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "ip_range", Value = "10.100.15.0/24" },
                    new() { Type = "hostname", Value = "*jnj*" },
                    new() { Type = "hostname", Value = "J-*" },
                    new() { Type = "url", Value = "*jnjservices.com*" },
                    new() { Type = "window_title", Value = "*InTime*" },
                    new() { Type = "window_title", Value = "*JNJ*" },
                }
            },
            new()
            {
                Name = "NLT (LearningMatters)",
                Code = "NLT",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "ip_range", Value = "192.168.100.0/24" },
                    new() { Type = "ip_range", Value = "192.168.44.0/22" },
                    new() { Type = "ip_range", Value = "192.168.92.0/24" },
                    new() { Type = "window_title", Value = "*FortiGate*" },
                    new() { Type = "window_title", Value = "*LearningMatters*" },
                    new() { Type = "hostname", Value = "*-eg*" },
                    new() { Type = "hostname", Value = "*-cg*" },
                    new() { Type = "hostname", Value = "*-em*" },
                }
            },
            new()
            {
                Name = "Hotel McCoy",
                Code = "HMC",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "ip_range", Value = "192.168.10.0/24" },
                    new() { Type = "url", Value = "*cloudbeds*" },
                    new() { Type = "hostname", Value = "*HMC*" },
                    new() { Type = "window_title", Value = "*Hotel McCoy*" },
                    new() { Type = "window_title", Value = "*Cloudbeds*" },
                }
            },
            new()
            {
                Name = "EP Builders",
                Code = "EPB",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "hostname", Value = "*EPB*" },
                    new() { Type = "url", Value = "*epbuilders.com*" },
                    new() { Type = "url", Value = "*ecoperformancebuilders.com*" },
                    new() { Type = "window_title", Value = "*EP Builders*" },
                }
            },
            new()
            {
                Name = "Carter Law",
                Code = "CLF",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "hostname", Value = "*CLF*" },
                    new() { Type = "window_title", Value = "*Carter*Law*" },
                }
            },
            new()
            {
                Name = "Shiloh Baptist Church",
                Code = "SBC",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "hostname", Value = "*shiloh*" },
                    new() { Type = "window_title", Value = "*Shiloh*" },
                }
            },
            new()
            {
                Name = "Tech Server Pro",
                Code = "TSP",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "ip_range", Value = "192.168.1.0/24" },
                    new() { Type = "hostname", Value = "claudellama*" },
                    new() { Type = "window_title", Value = "*MSP Portal*" },
                    new() { Type = "window_title", Value = "*MSP Autopilot*" },
                    new() { Type = "window_title", Value = "*techserverpro*" },
                }
            },
            // EdgeTech (ETG) end customers — billed to ETG via MachineDefaultClientCode on EdgeTech-PC;
            // these rules refine which EdgeTech customer the session is for.
            new()
            {
                Name = "Keech Law (EdgeTech client)",
                Code = "KEECH",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "hostname", Value = "*keech*" },
                    new() { Type = "hostname", Value = "KLF-*" },
                    new() { Type = "url", Value = "*keechlawfirm.com*" },
                    new() { Type = "window_title", Value = "*Keech*" },
                }
            },
            new()
            {
                Name = "Salter Construction (EdgeTech client)",
                Code = "SALTER",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "hostname", Value = "*salter*" },
                    new() { Type = "url", Value = "*salterconst.com*" },
                    new() { Type = "window_title", Value = "*Salter*" },
                }
            },
            // Tier3 MSP DIRECT clients (Tier3-VM) — billed individually, no parent rollup.
            new()
            {
                Name = "Additive CPA (Tier3 client)",
                Code = "ACPA",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "url", Value = "*additivecpa.com*" },
                    new() { Type = "hostname", Value = "*additive*" },
                    new() { Type = "window_title", Value = "*Additive CPA*" },
                }
            },
            new()
            {
                Name = "Joe Green / JG360 (Tier3 client)",
                Code = "JG360",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "url", Value = "*joegreenfitness360.com*" },
                    new() { Type = "url", Value = "*joegreenspeaks.com*" },
                    new() { Type = "url", Value = "*phyt4u.com*" },
                    new() { Type = "url", Value = "*parkinsonsexercisechecklist.com*" },
                    new() { Type = "window_title", Value = "*JG360*" },
                    new() { Type = "window_title", Value = "*Joe Green*" },
                }
            },
            new()
            {
                Name = "Stephen Snyder (Tier3 client)",
                Code = "SNYDER",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "ip_range", Value = "23.239.13.187/32" },
                    new() { Type = "ip_range", Value = "66.175.214.140/32" },
                    new() { Type = "ip_range", Value = "45.79.22.205/32" },
                    new() { Type = "ip_range", Value = "45.79.134.218/32" },
                    new() { Type = "hostname", Value = "*stephensnyder*" },
                    new() { Type = "hostname", Value = "*afterbankruptcy*" },
                    new() { Type = "hostname", Value = "*bnbformula*" },
                    new() { Type = "url", Value = "*stephensnyder.com*" },
                    new() { Type = "url", Value = "*afterbankruptcy.com*" },
                    new() { Type = "window_title", Value = "*Snyder*" },
                }
            },
            new()
            {
                Name = "Portage Sales (Tier3 client)",
                Code = "PORTAGE",
                Rules = new List<DetectionRule>
                {
                    new() { Type = "url", Value = "*portagesales.com*" },
                    new() { Type = "hostname", Value = "PETER-WS" },
                    new() { Type = "window_title", Value = "*Portage Sales*" },
                }
            },
        }
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}

/// <summary>
/// Client definition with detection rules
/// </summary>
public class ClientRule
{
    public string Name { get; set; } = "";
    public string Code { get; set; } = "";
    public List<DetectionRule> Rules { get; set; } = new();
}

/// <summary>
/// Individual detection rule
/// </summary>
public class DetectionRule
{
    /// <summary>Rule type: ip_range, hostname, window_title, url</summary>
    public string Type { get; set; } = "";

    /// <summary>Pattern or value to match</summary>
    public string Value { get; set; } = "";

    /// <summary>Compiled regex (cached)</summary>
    [JsonIgnore]
    public Regex? CompiledRegex { get; set; }

    /// <summary>Parsed IP network (cached)</summary>
    [JsonIgnore]
    public (IPAddress Network, IPAddress Mask)? ParsedNetwork { get; set; }

    /// <summary>Compile the pattern for faster matching</summary>
    public void Compile()
    {
        if (Type == "ip_range")
        {
            ParsedNetwork = ParseCidr(Value);
        }
        else
        {
            // Convert glob pattern to regex
            var pattern = "^" + Regex.Escape(Value)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";
            CompiledRegex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        }
    }

    private static (IPAddress, IPAddress)? ParseCidr(string cidr)
    {
        try
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2) return null;

            var ip = IPAddress.Parse(parts[0]);
            var prefixLength = int.Parse(parts[1]);

            var maskBytes = new byte[4];
            for (int i = 0; i < prefixLength; i++)
            {
                maskBytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
            var mask = new IPAddress(maskBytes);

            return (ip, mask);
        }
        catch
        {
            return null;
        }
    }
}
