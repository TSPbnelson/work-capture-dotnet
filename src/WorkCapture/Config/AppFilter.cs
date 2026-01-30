using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkCapture.Config;

/// <summary>
/// Configuration for filtering which apps to capture
/// </summary>
public class AppFilterConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("requireMaximized")]
    public bool RequireMaximized { get; set; } = false;

    [JsonPropertyName("allowedProcesses")]
    public List<string> AllowedProcesses { get; set; } = new();

    private HashSet<string>? _processLookup;

    /// <summary>
    /// Check if a process name is allowed for capture
    /// </summary>
    public bool IsAllowed(string? processName)
    {
        if (!Enabled || AllowedProcesses.Count == 0)
            return true; // No filter = allow all

        if (string.IsNullOrEmpty(processName))
            return false;

        // Build lookup set on first use
        _processLookup ??= new HashSet<string>(
            AllowedProcesses.Select(p => p.ToLowerInvariant()));

        // Check process name (case insensitive, without .exe)
        var name = processName.ToLowerInvariant().Replace(".exe", "");
        return _processLookup.Contains(name);
    }

    /// <summary>
    /// Load app filter configuration
    /// </summary>
    public static AppFilterConfig Load()
    {
        var paths = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkCapture", "config", "apps.json"),
            Path.Combine(AppContext.BaseDirectory, "config", "apps.json")
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var config = JsonSerializer.Deserialize<AppFilterConfig>(json);
                    if (config != null)
                    {
                        Logger.Info($"Loaded app filter from {path}: {config.AllowedProcesses.Count} apps");
                        return config;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to load app filter from {path}: {ex.Message}");
                }
            }
        }

        Logger.Info("No app filter config found, capturing all apps");
        return new AppFilterConfig { Enabled = false };
    }
}
