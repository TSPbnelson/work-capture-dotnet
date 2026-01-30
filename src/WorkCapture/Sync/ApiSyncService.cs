using System.Net.Http.Json;
using System.Text.Json;
using WorkCapture.Config;
using WorkCapture.Data;

namespace WorkCapture.Sync;

/// <summary>
/// Syncs local data to MSP Portal API
/// </summary>
public class ApiSyncService : IDisposable
{
    private readonly Database _db;
    private readonly SyncSettings _settings;
    private readonly HttpClient _client;

    private CancellationTokenSource? _cts;
    private Task? _syncTask;
    private DateTime? _lastSync;
    private int _syncErrors;
    private int _totalSynced;

    public ApiSyncService(Database db, SyncSettings settings)
    {
        _db = db;
        _settings = settings;

        _client = new HttpClient
        {
            BaseAddress = new Uri(settings.ApiUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        if (!string.IsNullOrEmpty(settings.ApiKey))
        {
            _client.DefaultRequestHeaders.Add("Authorization", $"Bearer {settings.ApiKey}");
        }
    }

    /// <summary>
    /// Start the sync service
    /// </summary>
    public void Start()
    {
        if (string.IsNullOrEmpty(_settings.ApiUrl))
        {
            Logger.Warning("No API URL configured - sync disabled");
            return;
        }

        _cts = new CancellationTokenSource();
        _syncTask = Task.Run(() => SyncLoop(_cts.Token));

        Logger.Info($"Sync service started, syncing to {_settings.ApiUrl}");
    }

    /// <summary>
    /// Stop the sync service
    /// </summary>
    public void Stop()
    {
        _cts?.Cancel();

        try
        {
            _syncTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Ignore cancellation exceptions
        }

        Logger.Info("Sync service stopped");
    }

    private async Task SyncLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await DoSync();
            }
            catch (Exception ex)
            {
                Logger.Error($"Sync error: {ex.Message}");
                _syncErrors++;
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_settings.SyncIntervalSeconds), token);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private async Task DoSync()
    {
        // Process pending queue items
        await ProcessSyncQueue();

        // Sync today's sessions
        var today = DateTime.Today;
        var sessions = AggregateSessions(today);

        foreach (var session in sessions)
        {
            await SyncSession(session);
        }

        _lastSync = DateTime.Now;
    }

    private async Task ProcessSyncQueue()
    {
        var items = _db.GetPendingSyncItems(20);

        foreach (var item in items)
        {
            try
            {
                await SyncQueueItem(item);
                _db.MarkSyncComplete(item.Id);
                _totalSynced++;
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to sync queue item {item.Id}: {ex.Message}");
                _db.MarkSyncFailed(item.Id, ex.Message);
            }
        }
    }

    private async Task SyncQueueItem(SyncQueueItem item)
    {
        var endpoint = item.EventType switch
        {
            "session" => "/work-capture/sessions",
            "entry" => "/work-capture/entries",
            _ => throw new ArgumentException($"Unknown event type: {item.EventType}")
        };

        var content = JsonContent.Create(
            JsonSerializer.Deserialize<object>(item.Payload));

        var response = await _client.PostAsync(endpoint, content);
        response.EnsureSuccessStatusCode();
    }

    private async Task SyncSession(SessionData session)
    {
        try
        {
            var payload = new
            {
                client_code = session.ClientCode,
                date = session.Date.ToString("yyyy-MM-dd"),
                start_time = session.StartTime.ToString("O"),
                end_time = session.EndTime.ToString("O"),
                duration_minutes = session.DurationMinutes,
                capture_count = session.CaptureCount,
                source = "work_capture_tool"
            };

            var response = await _client.PostAsJsonAsync("/work-capture/sessions", payload);

            if (response.IsSuccessStatusCode)
            {
                _totalSynced++;
                Logger.Debug($"Synced session: {session.ClientCode} {session.Date}");
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Logger.Warning($"Session sync failed: {response.StatusCode} - {error}");

                // Queue for retry
                _db.AddToSyncQueue("session", JsonSerializer.Serialize(payload));
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Session sync error: {ex.Message}");

            // Queue for retry
            _db.AddToSyncQueue("session", JsonSerializer.Serialize(new
            {
                client_code = session.ClientCode,
                date = session.Date.ToString("yyyy-MM-dd"),
                start_time = session.StartTime.ToString("O"),
                end_time = session.EndTime.ToString("O"),
                duration_minutes = session.DurationMinutes,
                capture_count = session.CaptureCount,
                source = "work_capture_tool"
            }));
        }
    }

    private List<SessionData> AggregateSessions(DateTime date)
    {
        var events = _db.GetEventsForDate(date);
        if (!events.Any())
            return new List<SessionData>();

        var sessions = new List<SessionData>();
        SessionData? current = null;
        var gapThreshold = TimeSpan.FromMinutes(15);

        foreach (var evt in events.OrderBy(e => e.Timestamp))
        {
            var client = evt.ClientCode ?? "GENERAL";

            bool startNew = current == null ||
                            current.ClientCode != client ||
                            (evt.Timestamp - current.EndTime) > gapThreshold;

            if (startNew)
            {
                if (current != null)
                {
                    current.DurationMinutes = (int)(current.EndTime - current.StartTime).TotalMinutes;
                    sessions.Add(current);
                }

                current = new SessionData
                {
                    ClientCode = client,
                    Date = date,
                    StartTime = evt.Timestamp,
                    EndTime = evt.Timestamp,
                    CaptureCount = 1
                };
            }
            else
            {
                current!.EndTime = evt.Timestamp;
                current.CaptureCount++;
            }
        }

        if (current != null)
        {
            current.DurationMinutes = (int)(current.EndTime - current.StartTime).TotalMinutes;
            sessions.Add(current);
        }

        return sessions;
    }

    /// <summary>
    /// Trigger immediate sync
    /// </summary>
    public async Task SyncNow()
    {
        try
        {
            await DoSync();
        }
        catch (Exception ex)
        {
            Logger.Error($"Manual sync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Get sync service status
    /// </summary>
    public SyncStatus GetStatus()
    {
        return new SyncStatus
        {
            Running = _syncTask?.Status == TaskStatus.Running,
            ApiUrl = _settings.ApiUrl,
            HasApiKey = !string.IsNullOrEmpty(_settings.ApiKey),
            LastSync = _lastSync,
            SyncErrors = _syncErrors,
            TotalSynced = _totalSynced,
            PendingQueue = _db.GetPendingSyncItems(100).Count
        };
    }

    public void Dispose()
    {
        Stop();
        _client.Dispose();
        _cts?.Dispose();
    }
}

/// <summary>
/// Session data for sync
/// </summary>
public class SessionData
{
    public string ClientCode { get; set; } = "";
    public DateTime Date { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int DurationMinutes { get; set; }
    public int CaptureCount { get; set; }
}

/// <summary>
/// Sync service status
/// </summary>
public class SyncStatus
{
    public bool Running { get; set; }
    public string ApiUrl { get; set; } = "";
    public bool HasApiKey { get; set; }
    public DateTime? LastSync { get; set; }
    public int SyncErrors { get; set; }
    public int TotalSynced { get; set; }
    public int PendingQueue { get; set; }
}
