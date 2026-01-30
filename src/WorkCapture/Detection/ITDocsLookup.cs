using System.Net.Http.Json;
using System.Text.Json.Serialization;
using WorkCapture.Data;

namespace WorkCapture.Detection;

/// <summary>
/// Looks up assets in ITDocs to identify clients
/// </summary>
public class ITDocsLookup : IDisposable
{
    private readonly HttpClient _client;
    private readonly Dictionary<string, CachedLookup> _cache = new();
    private readonly TimeSpan _cacheExpiry = TimeSpan.FromMinutes(30);

    public ITDocsLookup(string apiUrl)
    {
        _client = new HttpClient
        {
            BaseAddress = new Uri(apiUrl.TrimEnd('/')),
            Timeout = TimeSpan.FromSeconds(5)
        };
    }

    /// <summary>
    /// Look up client by window title
    /// </summary>
    public async Task<ClientMatch?> LookupByWindowTitle(string windowTitle)
    {
        // Check cache first
        var cacheKey = $"title:{windowTitle}";
        if (TryGetCached(cacheKey, out var cached))
            return cached;

        try
        {
            var encoded = Uri.EscapeDataString(windowTitle);
            var response = await _client.GetAsync($"/api/asset-lookup/window-title?title={encoded}");

            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<LookupResponse>();

            if (result?.Found == true && result.Match != null)
            {
                var match = new ClientMatch
                {
                    ClientName = result.Match.ClientName,
                    ClientCode = result.Match.ClientCode,
                    Confidence = result.Match.Confidence,
                    MatchedRule = $"itdocs:{result.Match.AssetHostname}",
                    MatchedValue = result.Match.AssetName ?? result.Match.AssetHostname
                };

                CacheResult(cacheKey, match);
                return match;
            }

            // Cache negative result too
            CacheResult(cacheKey, null);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"ITDocs lookup failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Look up client by hostname
    /// </summary>
    public async Task<ClientMatch?> LookupByHostname(string hostname)
    {
        var cacheKey = $"host:{hostname}";
        if (TryGetCached(cacheKey, out var cached))
            return cached;

        try
        {
            var response = await _client.GetAsync($"/api/asset-lookup/hostname/{Uri.EscapeDataString(hostname)}");

            if (!response.IsSuccessStatusCode)
                return null;

            var result = await response.Content.ReadFromJsonAsync<LookupResponse>();

            if (result?.Found == true && result.Match != null)
            {
                var match = new ClientMatch
                {
                    ClientName = result.Match.ClientName,
                    ClientCode = result.Match.ClientCode,
                    Confidence = result.Match.Confidence,
                    MatchedRule = $"itdocs:{result.Match.AssetHostname}",
                    MatchedValue = result.Match.AssetName ?? result.Match.AssetHostname
                };

                CacheResult(cacheKey, match);
                return match;
            }

            CacheResult(cacheKey, null);
            return null;
        }
        catch (Exception ex)
        {
            Logger.Debug($"ITDocs lookup failed: {ex.Message}");
            return null;
        }
    }

    private bool TryGetCached(string key, out ClientMatch? match)
    {
        if (_cache.TryGetValue(key, out var cached))
        {
            if (DateTime.Now - cached.Timestamp < _cacheExpiry)
            {
                match = cached.Match;
                return true;
            }
            _cache.Remove(key);
        }

        match = null;
        return false;
    }

    private void CacheResult(string key, ClientMatch? match)
    {
        _cache[key] = new CachedLookup
        {
            Match = match,
            Timestamp = DateTime.Now
        };

        // Clean old entries periodically
        if (_cache.Count > 1000)
        {
            var expired = _cache
                .Where(kv => DateTime.Now - kv.Value.Timestamp > _cacheExpiry)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var k in expired)
                _cache.Remove(k);
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }

    private class CachedLookup
    {
        public ClientMatch? Match { get; set; }
        public DateTime Timestamp { get; set; }
    }
}

/// <summary>
/// Response from ITDocs lookup API
/// </summary>
public class LookupResponse
{
    [JsonPropertyName("found")]
    public bool Found { get; set; }

    [JsonPropertyName("match")]
    public LookupMatch? Match { get; set; }
}

public class LookupMatch
{
    [JsonPropertyName("client_id")]
    public string ClientId { get; set; } = "";

    [JsonPropertyName("client_name")]
    public string ClientName { get; set; } = "";

    [JsonPropertyName("client_code")]
    public string ClientCode { get; set; } = "";

    [JsonPropertyName("asset_hostname")]
    public string AssetHostname { get; set; } = "";

    [JsonPropertyName("asset_name")]
    public string? AssetName { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }
}
