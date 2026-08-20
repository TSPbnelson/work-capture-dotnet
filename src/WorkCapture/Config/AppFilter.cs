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
    /// Check if a process name is allowed for capture.
    ///
    /// ⛔ UNKNOWN MUST NEVER MEAN BLOCKED. An empty process name used to return false, so any
    /// frame whose foreground process could not be resolved was dropped — and the caller logs
    /// it as "Skip: app_filtered" without counting it, so the skip counter stayed at 1 all day.
    /// On 2026-08-18 that silently destroyed 1,228 frames on TIER3-VM: 2h23m of EasyITGuys work
    /// that Brad only discovered by noticing the client missing from Work Review.
    ///
    /// WindowInfo.GetActiveWindowInfo() returns a blank record whenever GetForegroundWindow()
    /// gives IntPtr.Zero or Process.GetProcessById throws, both of which happen in normal use
    /// (locked session, secure desktop, a process in another session). Losing billable time to
    /// that is never the right trade: capture the frame and let the privacy denylist — which is
    /// metadata-only and preserves the time — decide what must not be photographed.
    /// </summary>
    public bool IsAllowed(string? processName)
    {
        if (!Enabled || AllowedProcesses.Count == 0)
            return true; // No filter = allow all

        if (string.IsNullOrEmpty(processName))
            return true; // Unknown process — capture it. Never drop billable time on ignorance.

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
            @"C:\WorkCapture\config\apps.json",
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

        // Create default config file
        var defaultConfig = CreateDefault();
        var configPath = @"C:\WorkCapture\config\apps.json";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            var json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configPath, json);
            Logger.Info($"Created default app filter at {configPath}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not create default app filter: {ex.Message}");
        }

        return defaultConfig;
    }

    /// <summary>
    /// Create default app filter configuration
    /// </summary>
    public static AppFilterConfig CreateDefault()
    {
        return new AppFilterConfig
        {
            // Denylist model: whitelist disabled so no app is ever silently dropped.
            // Capture gating is handled by privacy.json (what NOT to capture), not this list.
            Enabled = false,
            RequireMaximized = false,
            AllowedProcesses = new List<string>
            {
                "chrome",
                "msedge",
                "firefox",
                "mstsc",
                "strwinclt",
                "isl online for superops",
                "islonlineagentforsuperops",
                "isllight",
                "putty",
                "winscp",
                "code",
                "devenv",
                "ssms",
                "WSM",
                "powershell",
                "WindowsTerminal"
            }
        };
    }
}
