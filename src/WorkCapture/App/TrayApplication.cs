using System.Drawing;
using WorkCapture.Capture;
using WorkCapture.Config;
using WorkCapture.Data;
using WorkCapture.Detection;
using WorkCapture.Sync;

namespace WorkCapture.App;

/// <summary>
/// Main application with system tray icon
/// </summary>
public class TrayApplication : IDisposable
{
    private readonly Settings _settings;

    // Components
    private readonly Database _db;
    private readonly ScreenCapture _screenCapture;
    private readonly WindowInfoExtractor _windowExtractor;
    private readonly ActivityMonitor _activityMonitor;
    private readonly ClientDetector _clientDetector;
    private readonly PrivacyFilter _privacyFilter;
    private readonly ChangeDetector _changeDetector;
    private readonly AdaptiveCaptureRate _adaptiveRate;
    private readonly ApiSyncService _syncService;

    // Tray
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _contextMenu;

    // State
    private bool _running;
    private bool _paused;
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private int _captureCount;
    private int _skipCount;

    public TrayApplication(Settings settings)
    {
        _settings = settings;

        // Initialize components
        _db = new Database(settings.Storage.DatabasePath);

        _screenCapture = new ScreenCapture(
            settings.Storage.ScreenshotDir,
            settings.Capture.ScreenshotFormat,
            settings.Capture.ScreenshotQuality,
            settings.Capture.MaxScreenshotWidth);

        _windowExtractor = new WindowInfoExtractor();

        _activityMonitor = new ActivityMonitor(settings.Capture.KeyboardActivityWindowMs);

        var clientRules = ClientRulesConfig.Load();
        _clientDetector = new ClientDetector(clientRules, settings.Sync.ApiUrl);

        var privacyRules = PrivacyRulesConfig.Load();
        _privacyFilter = new PrivacyFilter(privacyRules);

        _changeDetector = new ChangeDetector(
            settings.Capture.ChangeDetectionThreshold,
            minIntervalSeconds: 2,
            maxIntervalSeconds: settings.Capture.CaptureIntervalSeconds * 6);

        _adaptiveRate = new AdaptiveCaptureRate(
            settings.Capture.CaptureIntervalSeconds,
            minInterval: 2.0,
            maxInterval: 30.0);

        _syncService = new ApiSyncService(_db, settings.Sync);

        // Load last hash for continuity
        var lastHash = _db.GetLastImageHash();
        if (lastHash != null)
        {
            _changeDetector.SetLastHash(lastHash);
        }
    }

    /// <summary>
    /// Run the application
    /// </summary>
    public void Run()
    {
        // Create tray icon
        CreateTrayIcon();

        // Start capture
        Start();

        // Run message loop (required for tray icon)
        Application.Run();
    }

    private void CreateTrayIcon()
    {
        // Create context menu
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add("Status: Running").Enabled = false;
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Pause", null, OnPauseClick);
        _contextMenu.Items.Add("Resume", null, OnResumeClick);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Force Capture", null, OnForceCaptureClick);
        _contextMenu.Items.Add("Sync Now", null, OnSyncNowClick);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Show Stats", null, OnShowStatsClick);
        _contextMenu.Items.Add("Screenshots", null, OnScreenshotsClick);
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("Quit", null, OnQuitClick);

        // Create tray icon
        _trayIcon = new NotifyIcon
        {
            Icon = CreateIcon(Color.LimeGreen),
            Text = "Work Capture - Running",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        _trayIcon.DoubleClick += OnShowStatsClick;
    }

    private static Icon CreateIcon(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(color);
        graphics.FillEllipse(brush, 1, 1, 14, 14);
        return Icon.FromHandle(bitmap.GetHicon());
    }

    private void UpdateTrayIcon(Color color, string status)
    {
        if (_trayIcon != null)
        {
            _trayIcon.Icon = CreateIcon(color);
            _trayIcon.Text = $"Work Capture - {status}";
        }

        if (_contextMenu?.Items.Count > 0)
        {
            _contextMenu.Items[0].Text = $"Status: {status}";
        }
    }

    private void Start()
    {
        if (_running) return;

        _running = true;
        _paused = false;

        // Start activity monitoring
        _activityMonitor.Start();

        // Start sync service
        _syncService.Start();

        // Start capture loop
        _cts = new CancellationTokenSource();
        _captureTask = Task.Run(() => CaptureLoop(_cts.Token));

        UpdateTrayIcon(Color.LimeGreen, "Running");
        Logger.Info("Capture started");
    }

    private void Stop()
    {
        _running = false;

        // Stop capture
        _cts?.Cancel();
        _captureTask?.Wait(TimeSpan.FromSeconds(5));

        // Stop activity monitoring
        _activityMonitor.Stop();

        // Stop sync
        _syncService.Stop();

        // Cleanup
        RunCleanup();

        UpdateTrayIcon(Color.Gray, "Stopped");
        Logger.Info("Capture stopped");
    }

    private async Task CaptureLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (!_paused)
                {
                    DoCaptureCheck();
                }

                var interval = TimeSpan.FromSeconds(_adaptiveRate.CurrentInterval);
                await Task.Delay(interval, token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Error($"Capture loop error: {ex.Message}");
                await Task.Delay(5000, token);
            }
        }
    }

    private void DoCaptureCheck()
    {
        // Get window info
        var windowInfo = _windowExtractor.GetActiveWindowInfo();

        // Check privacy filter
        var (excluded, reason) = _privacyFilter.ShouldExclude(
            windowInfo.ProcessName,
            windowInfo.Title,
            windowInfo.Url);

        if (excluded)
        {
            Logger.Debug($"Excluded: {reason}");
            _skipCount++;
            return;
        }

        // Get activity state
        var keyboardActive = _activityMonitor.IsKeyboardActive;
        var mouseActive = _activityMonitor.IsMouseActive;

        // Update adaptive rate
        _adaptiveRate.UpdateActivity(keyboardActive, mouseActive);

        // Check if we should capture
        var (shouldCapture, captureReason) = _changeDetector.ShouldCapture(
            windowInfo.Title,
            windowInfo.ProcessName,
            keyboardActive: keyboardActive && _settings.Capture.CaptureOnKeyboardActivity,
            mouseActive: mouseActive);

        if (!shouldCapture)
        {
            Logger.Debug($"Skipping: {captureReason}");
            _skipCount++;
            return;
        }

        // Detect client
        var clientMatch = _clientDetector.Detect(
            windowTitle: windowInfo.Title,
            hostname: windowInfo.Hostname,
            url: windowInfo.Url);

        // Determine capture type
        string captureType;
        string? screenshotPath = null;
        string? imageHash = null;

        if (_privacyFilter.IsSensitiveContent(windowInfo.Title, windowInfo.Url))
        {
            captureType = "metadata_only";
        }
        else
        {
            var result = _screenCapture.Capture();
            if (result != null)
            {
                screenshotPath = result.Value.Path;
                imageHash = result.Value.Hash;
                captureType = "full";
            }
            else
            {
                captureType = "failed";
            }
        }

        // Record in database
        var evt = new CaptureEvent
        {
            Timestamp = DateTime.Now,
            WindowTitle = windowInfo.Title,
            ProcessName = windowInfo.ProcessName,
            Url = windowInfo.Url,
            Hostname = windowInfo.Hostname,
            ClientCode = clientMatch?.ClientCode,
            ClientConfidence = clientMatch?.Confidence ?? 0,
            CaptureType = captureType,
            CaptureReason = captureReason,
            ScreenshotPath = screenshotPath,
            ImageHash = imageHash,
            KeyboardActive = keyboardActive,
            MouseActive = mouseActive
        };

        _db.InsertCaptureEvent(evt);

        // Update change detector
        _changeDetector.RecordCapture(windowInfo.Title, windowInfo.ProcessName, imageHash);

        _captureCount++;

        Logger.Debug($"Captured: {captureType} | {windowInfo.ProcessName} | " +
                     $"Client: {clientMatch?.ClientCode ?? "Unknown"} | Reason: {captureReason}");
    }

    private void ForceCapture()
    {
        if (_paused) return;

        var windowInfo = _windowExtractor.GetActiveWindowInfo();
        var clientMatch = _clientDetector.Detect(
            windowTitle: windowInfo.Title,
            hostname: windowInfo.Hostname);

        var result = _screenCapture.Capture();

        var evt = new CaptureEvent
        {
            Timestamp = DateTime.Now,
            WindowTitle = windowInfo.Title,
            ProcessName = windowInfo.ProcessName,
            Hostname = windowInfo.Hostname,
            ClientCode = clientMatch?.ClientCode,
            ClientConfidence = clientMatch?.Confidence ?? 0,
            CaptureType = result != null ? "full" : "failed",
            CaptureReason = "forced",
            ScreenshotPath = result?.Path,
            ImageHash = result?.Hash,
            KeyboardActive = _activityMonitor.IsKeyboardActive,
            MouseActive = _activityMonitor.IsMouseActive
        };

        _db.InsertCaptureEvent(evt);
        _captureCount++;

        Logger.Info("Forced capture completed");
    }

    private void RunCleanup()
    {
        _db.CleanupOldData(_settings.Capture.RetentionDays);
        ScreenshotCleanup.CleanupOldScreenshots(
            _settings.Storage.ScreenshotDir,
            _settings.Capture.RetentionDays);
    }

    #region Event Handlers

    private void OnPauseClick(object? sender, EventArgs e)
    {
        _paused = true;
        UpdateTrayIcon(Color.Yellow, "Paused");
        Logger.Info("Capture paused");
    }

    private void OnResumeClick(object? sender, EventArgs e)
    {
        _paused = false;
        UpdateTrayIcon(Color.LimeGreen, "Running");
        Logger.Info("Capture resumed");
    }

    private void OnForceCaptureClick(object? sender, EventArgs e)
    {
        ForceCapture();
    }

    private async void OnSyncNowClick(object? sender, EventArgs e)
    {
        await _syncService.SyncNow();
        Logger.Info("Manual sync triggered");
    }

    private void OnScreenshotsClick(object? sender, EventArgs e)
    {
        var path = _settings.Storage.ScreenshotDir;
        if (Directory.Exists(path))
        {
            System.Diagnostics.Process.Start("explorer.exe", path);
        }
        else
        {
            MessageBox.Show($"Screenshots folder not found:\n{path}", "Work Capture",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OnShowStatsClick(object? sender, EventArgs e)
    {
        var dbStats = _db.GetStats();
        var syncStatus = _syncService.GetStatus();

        var message =
            $"Captures: {_captureCount}\n" +
            $"Skipped: {_skipCount}\n" +
            $"Today's events: {dbStats.TodayEvents}\n" +
            $"Unsynced: {dbStats.UnsyncedEvents}\n" +
            $"Sync pending: {syncStatus.PendingQueue}\n" +
            $"Activity level: {_adaptiveRate.ActivityLevel:P0}\n\n" +
            $"Clients today:\n" +
            string.Join("\n", dbStats.TodayByClient.Select(kv => $"  {kv.Key}: {kv.Value}"));

        MessageBox.Show(message, "Work Capture Stats", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void OnQuitClick(object? sender, EventArgs e)
    {
        Stop();
        _trayIcon!.Visible = false;
        Application.Exit();
    }

    #endregion

    public void Dispose()
    {
        Stop();
        _trayIcon?.Dispose();
        _contextMenu?.Dispose();
        _screenCapture.Dispose();
        _activityMonitor.Dispose();
        _clientDetector.Dispose();
        _syncService.Dispose();
        _db.Dispose();
        _cts?.Dispose();
    }
}
