using WorkCapture.App;
using WorkCapture.Config;

namespace WorkCapture;

/// <summary>
/// Work Capture - Real-time work tracking for MSP billing
/// </summary>
internal static class Program
{
    /// <summary>
    /// Application entry point
    /// </summary>
    [STAThread]
    static void Main(string[] args)
    {
        // Ensure single instance
        using var mutex = new Mutex(true, "WorkCapture_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Work Capture is already running.", "Work Capture",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        // Enable visual styles for modern UI
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        // Handle unhandled exceptions
        Application.ThreadException += (s, e) =>
        {
            Logger.Error($"Unhandled thread exception: {e.Exception}");
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Logger.Error($"Unhandled domain exception: {e.ExceptionObject}");
        };

        try
        {
            // Load configuration
            var settings = Settings.Load();
            Logger.Initialize(settings.Storage.LogPath);
            Logger.Info("Work Capture starting...");

            // Create and run the tray application
            using var app = new TrayApplication(settings);
            app.Run();

            Logger.Info("Work Capture stopped.");
        }
        catch (Exception ex)
        {
            Logger.Error($"Fatal error: {ex}");
            MessageBox.Show($"Fatal error: {ex.Message}", "Work Capture Error",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

/// <summary>
/// Simple file logger
/// </summary>
public static class Logger
{
    private static string? _logPath;
    private static readonly object _lock = new();

    public static void Initialize(string logDirectory)
    {
        Directory.CreateDirectory(logDirectory);
        _logPath = Path.Combine(logDirectory, $"WorkCapture_{DateTime.Now:yyyy-MM-dd}.log");
    }

    public static void Info(string message) => Log("INFO", message);
    public static void Warning(string message) => Log("WARN", message);
    public static void Error(string message) => Log("ERROR", message);
    public static void Debug(string message)
    {
#if DEBUG
        Log("DEBUG", message);
#endif
    }

    private static void Log(string level, string message)
    {
        if (_logPath == null) return;

        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";

        lock (_lock)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch
            {
                // Ignore logging errors
            }
        }

#if DEBUG
        System.Diagnostics.Debug.WriteLine(line);
#endif
    }
}
