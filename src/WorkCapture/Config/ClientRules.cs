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
