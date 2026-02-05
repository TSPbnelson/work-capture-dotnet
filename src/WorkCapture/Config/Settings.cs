using System.Text.Json;
using System.Text.Json.Serialization;

namespace WorkCapture.Config;

/// <summary>
/// Main application settings
/// </summary>
public class Settings
{
    public CaptureSettings Capture { get; set; } = new();
    public SyncSettings Sync { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
    public VisionSettings Vision { get; set; } = new();

    private static readonly string DataConfigDir = @"C:\WorkCapture\config";
    private static readonly string AppConfigDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config");

    public static Settings Load()
    {
        // Check data directory first, then app directory
        var paths = new[]
        {
            Path.Combine(DataConfigDir, "settings.json"),
            Path.Combine(AppConfigDir, "settings.json")
        };

        foreach (var path in paths)
        {
            if (File.Exists(path))
            {
                try
                {
                    var json = File.ReadAllText(path);
                    var settings = JsonSerializer.Deserialize<Settings>(json, JsonOptions) ?? new Settings();
                    settings.Storage.Initialize();
                    Logger.Info($"Loaded settings from {path}");
                    return settings;
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Failed to load settings from {path}: {ex.Message}");
                }
            }
        }

        // Create default settings
        var defaultSettings = new Settings();
        defaultSettings.Storage.Initialize();

        // Save defaults to data directory
        try
        {
            Directory.CreateDirectory(DataConfigDir);
            var json = JsonSerializer.Serialize(defaultSettings, JsonOptions);
            var savePath = Path.Combine(DataConfigDir, "settings.json");
            File.WriteAllText(savePath, json);
            Logger.Info($"Created default settings at {savePath}");
        }
        catch (Exception ex)
        {
            Logger.Warning($"Could not save default settings: {ex.Message}");
        }

        return defaultSettings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataConfigDir);
            var json = JsonSerializer.Serialize(this, JsonOptions);
            File.WriteAllText(Path.Combine(DataConfigDir, "settings.json"), json);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to save settings: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>
/// Screenshot capture settings
/// </summary>
public class CaptureSettings
{
    /// <summary>Seconds between capture checks</summary>
    public int CaptureIntervalSeconds { get; set; } = 5;

    /// <summary>Capture when window title changes</summary>
    public bool CaptureOnWindowChange { get; set; } = true;

    /// <summary>Capture when keyboard is active</summary>
    public bool CaptureOnKeyboardActivity { get; set; } = true;

    /// <summary>Milliseconds to consider keyboard "active"</summary>
    public int KeyboardActivityWindowMs { get; set; } = 500;

    /// <summary>Image format: webp, png, jpg</summary>
    public string ScreenshotFormat { get; set; } = "webp";

    /// <summary>Image quality (1-100)</summary>
    public int ScreenshotQuality { get; set; } = 85;

    /// <summary>Maximum width in pixels (height scales proportionally)</summary>
    public int MaxScreenshotWidth { get; set; } = 1920;

    /// <summary>Days to keep screenshots before cleanup</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Hash difference threshold (0-64, lower = stricter)</summary>
    public int ChangeDetectionThreshold { get; set; } = 5;

    /// <summary>Seconds of inactivity before pausing captures (0 = disabled)</summary>
    public int IdleTimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// MSP Portal sync settings
/// </summary>
public class SyncSettings
{
    /// <summary>MSP Portal API base URL</summary>
    public string ApiUrl { get; set; } = "https://msp.techserverpro.com/api";

    /// <summary>API authentication key</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Seconds between sync attempts</summary>
    public int SyncIntervalSeconds { get; set; } = 60;

    /// <summary>Whether to sync screenshot files (large)</summary>
    public bool SyncScreenshots { get; set; } = false;
}

/// <summary>
/// Local storage settings
/// </summary>
public class StorageSettings
{
    /// <summary>Base data directory</summary>
    public string DataDir { get; set; } = "";

    /// <summary>Screenshot storage directory</summary>
    public string ScreenshotDir { get; set; } = "";

    /// <summary>SQLite database path</summary>
    public string DatabasePath { get; set; } = "";

    /// <summary>Log file directory</summary>
    public string LogPath { get; set; } = "";

    /// <summary>Initialize paths if not set</summary>
    public void Initialize()
    {
        if (string.IsNullOrEmpty(DataDir))
        {
            // Use C:\WorkCapture for all data
            DataDir = @"C:\WorkCapture";
        }

        if (string.IsNullOrEmpty(ScreenshotDir))
            ScreenshotDir = Path.Combine(DataDir, "screenshots");

        if (string.IsNullOrEmpty(DatabasePath))
            DatabasePath = Path.Combine(DataDir, "workcapture.db");

        if (string.IsNullOrEmpty(LogPath))
            LogPath = Path.Combine(DataDir, "logs");

        // Ensure directories exist
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ScreenshotDir);
        Directory.CreateDirectory(LogPath);
    }
}

/// <summary>
/// Vision analysis settings
/// </summary>
public class VisionSettings
{
    /// <summary>Enable vision analysis</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Vision analysis service URL</summary>
    public string ServiceUrl { get; set; } = "http://192.168.1.16:8001";

    /// <summary>Request timeout in seconds (45s to accommodate Ollama fallback)</summary>
    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>Analyze every Nth capture (1 = all, 5 = every 5th)</summary>
    public int AnalyzeEveryNth { get; set; } = 1;

    /// <summary>Skip analysis if previous analysis confidence was high</summary>
    public bool SkipHighConfidenceRepeats { get; set; } = true;

    /// <summary>Minimum confidence threshold for skipping repeats</summary>
    public double HighConfidenceThreshold { get; set; } = 0.85;

    /// <summary>Run analysis asynchronously (don't block capture loop)</summary>
    public bool AsyncAnalysis { get; set; } = true;
}
