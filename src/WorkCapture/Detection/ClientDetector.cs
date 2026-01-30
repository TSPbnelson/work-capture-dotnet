using System.Net;
using WorkCapture.Config;
using WorkCapture.Data;

namespace WorkCapture.Detection;

/// <summary>
/// Detects which client work belongs to based on ITDocs lookup + local rules
/// </summary>
public class ClientDetector : IDisposable
{
    private readonly List<ClientRule> _clients;
    private readonly ITDocsLookup? _itdocsLookup;

    public ClientDetector(ClientRulesConfig config, string? apiUrl = null)
    {
        _clients = config.Clients;

        // Compile all patterns for local rules
        foreach (var client in _clients)
        {
            foreach (var rule in client.Rules)
            {
                rule.Compile();
            }
        }

        // Initialize ITDocs lookup if API URL provided
        if (!string.IsNullOrEmpty(apiUrl))
        {
            _itdocsLookup = new ITDocsLookup(apiUrl);
            Logger.Info($"ITDocs lookup enabled: {apiUrl}");
        }
    }

    /// <summary>
    /// Detect client from available information
    /// Uses ITDocs API first, falls back to local rules
    /// </summary>
    public ClientMatch? Detect(
        string? windowTitle = null,
        string? hostname = null,
        string? url = null,
        string? ipAddress = null)
    {
        // Try ITDocs lookup first (async but we block here for simplicity)
        if (_itdocsLookup != null)
        {
            var itdocsMatch = DetectFromITDocsAsync(windowTitle, hostname).GetAwaiter().GetResult();
            if (itdocsMatch != null)
            {
                Logger.Debug($"ITDocs match: {itdocsMatch.ClientCode} ({itdocsMatch.MatchedValue})");
                return itdocsMatch;
            }
        }

        // Fall back to local rules
        return DetectFromLocalRules(windowTitle, hostname, url, ipAddress);
    }

    /// <summary>
    /// Async detection for use in async contexts
    /// </summary>
    public async Task<ClientMatch?> DetectAsync(
        string? windowTitle = null,
        string? hostname = null,
        string? url = null,
        string? ipAddress = null)
    {
        // Try ITDocs lookup first
        if (_itdocsLookup != null)
        {
            var itdocsMatch = await DetectFromITDocsAsync(windowTitle, hostname);
            if (itdocsMatch != null)
            {
                Logger.Debug($"ITDocs match: {itdocsMatch.ClientCode} ({itdocsMatch.MatchedValue})");
                return itdocsMatch;
            }
        }

        // Fall back to local rules
        return DetectFromLocalRules(windowTitle, hostname, url, ipAddress);
    }

    private async Task<ClientMatch?> DetectFromITDocsAsync(string? windowTitle, string? hostname)
    {
        if (_itdocsLookup == null)
            return null;

        // Try window title first (extracts hostname patterns)
        if (!string.IsNullOrEmpty(windowTitle))
        {
            var match = await _itdocsLookup.LookupByWindowTitle(windowTitle);
            if (match != null)
                return match;
        }

        // Try hostname if available
        if (!string.IsNullOrEmpty(hostname))
        {
            var match = await _itdocsLookup.LookupByHostname(hostname);
            if (match != null)
                return match;
        }

        return null;
    }

    private ClientMatch? DetectFromLocalRules(
        string? windowTitle,
        string? hostname,
        string? url,
        string? ipAddress)
    {
        ClientMatch? bestMatch = null;

        foreach (var client in _clients)
        {
            var match = CheckClient(client, windowTitle, hostname, url, ipAddress);
            if (match != null && (bestMatch == null || match.Confidence > bestMatch.Confidence))
            {
                bestMatch = match;
            }
        }

        return bestMatch;
    }

    /// <summary>
    /// Get all matching clients sorted by confidence
    /// </summary>
    public List<ClientMatch> DetectAll(
        string? windowTitle = null,
        string? hostname = null,
        string? url = null,
        string? ipAddress = null)
    {
        var matches = new List<ClientMatch>();

        foreach (var client in _clients)
        {
            var match = CheckClient(client, windowTitle, hostname, url, ipAddress);
            if (match != null)
            {
                matches.Add(match);
            }
        }

        return matches.OrderByDescending(m => m.Confidence).ToList();
    }

    private ClientMatch? CheckClient(
        ClientRule client,
        string? windowTitle,
        string? hostname,
        string? url,
        string? ipAddress)
    {
        ClientMatch? bestMatch = null;

        foreach (var rule in client.Rules)
        {
            double confidence = 0;
            string matchedValue = "";

            switch (rule.Type)
            {
                case "ip_range" when !string.IsNullOrEmpty(ipAddress):
                    if (IsIpInRange(ipAddress, rule))
                    {
                        confidence = 0.95;
                        matchedValue = ipAddress;
                    }
                    break;

                case "hostname" when !string.IsNullOrEmpty(hostname):
                    if (rule.CompiledRegex?.IsMatch(hostname) == true)
                    {
                        confidence = 0.90;
                        matchedValue = hostname;
                    }
                    break;

                case "window_title" when !string.IsNullOrEmpty(windowTitle):
                    if (rule.CompiledRegex?.IsMatch(windowTitle) == true)
                    {
                        confidence = 0.75;
                        matchedValue = windowTitle;
                    }
                    break;

                case "url" when !string.IsNullOrEmpty(url):
                    if (rule.CompiledRegex?.IsMatch(url) == true)
                    {
                        confidence = 0.85;
                        matchedValue = url;
                    }
                    break;
            }

            if (confidence > 0 && (bestMatch == null || confidence > bestMatch.Confidence))
            {
                bestMatch = new ClientMatch
                {
                    ClientName = client.Name,
                    ClientCode = client.Code,
                    Confidence = confidence,
                    MatchedRule = $"{rule.Type}:{rule.Value}",
                    MatchedValue = matchedValue
                };
            }
        }

        return bestMatch;
    }

    private static bool IsIpInRange(string ipAddress, DetectionRule rule)
    {
        if (rule.ParsedNetwork == null)
            return false;

        try
        {
            var ip = IPAddress.Parse(ipAddress);
            var (network, mask) = rule.ParsedNetwork.Value;

            var ipBytes = ip.GetAddressBytes();
            var networkBytes = network.GetAddressBytes();
            var maskBytes = mask.GetAddressBytes();

            for (int i = 0; i < 4; i++)
            {
                if ((ipBytes[i] & maskBytes[i]) != (networkBytes[i] & maskBytes[i]))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _itdocsLookup?.Dispose();
    }
}
