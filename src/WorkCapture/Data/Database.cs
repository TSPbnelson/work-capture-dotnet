using Microsoft.Data.Sqlite;

namespace WorkCapture.Data;

/// <summary>
/// SQLite database for local capture storage
/// </summary>
public class Database : IDisposable
{
    private readonly string _connectionString;

    public Database(string dbPath)
    {
        _connectionString = $"Data Source={dbPath}";
        Initialize();
    }

    private void Initialize()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            CREATE TABLE IF NOT EXISTS capture_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                window_title TEXT,
                process_name TEXT,
                url TEXT,
                hostname TEXT,
                client_code TEXT,
                client_confidence REAL,
                capture_type TEXT,
                capture_reason TEXT,
                screenshot_path TEXT,
                image_hash TEXT,
                keyboard_active INTEGER DEFAULT 0,
                mouse_active INTEGER DEFAULT 0,
                synced INTEGER DEFAULT 0,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS work_sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                client_code TEXT,
                date TEXT NOT NULL,
                start_time TEXT,
                end_time TEXT,
                duration_minutes INTEGER,
                capture_count INTEGER,
                work_description TEXT,
                synced INTEGER DEFAULT 0,
                sync_id TEXT,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP
            );

            CREATE TABLE IF NOT EXISTS sync_queue (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                event_type TEXT NOT NULL,
                payload TEXT NOT NULL,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP,
                synced_at TEXT,
                status TEXT DEFAULT 'pending',
                error_message TEXT,
                retry_count INTEGER DEFAULT 0
            );

            CREATE TABLE IF NOT EXISTS screenshot_uploads (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                screenshot_path TEXT UNIQUE NOT NULL,
                uploaded_at TEXT DEFAULT CURRENT_TIMESTAMP,
                file_size_bytes INTEGER,
                client_code TEXT,
                upload_status TEXT DEFAULT 'success'
            );

            -- Typed text AFTER WorkCapture.Privacy redaction. Raw keystrokes are never stored.
            CREATE TABLE IF NOT EXISTS typed_text (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                start_time TEXT NOT NULL,
                end_time TEXT NOT NULL,
                process_name TEXT,
                window_title TEXT,
                text TEXT NOT NULL,
                terminator TEXT,
                redactions TEXT,
                suppressed INTEGER DEFAULT 0,
                suppress_reason TEXT,
                key_count INTEGER DEFAULT 0,
                shortcuts TEXT,
                synced INTEGER DEFAULT 0,
                created_at TEXT DEFAULT CURRENT_TIMESTAMP
            );

            -- Activity stream: input counts per 5 s bucket (idle stretches merged). Minutes are
            -- computed from this server-side, not from screenshot cadence.
            CREATE TABLE IF NOT EXISTS activity_buckets (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                start_time TEXT NOT NULL,
                duration_s INTEGER NOT NULL,
                keys INTEGER DEFAULT 0,
                clicks INTEGER DEFAULT 0,
                scrolls INTEGER DEFAULT 0,
                mouse_moved INTEGER DEFAULT 0,
                last_input_age_ms INTEGER,
                process_name TEXT,
                window_title TEXT,
                session_state TEXT,
                synced INTEGER DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS idx_activity_synced ON activity_buckets(synced, id);

            CREATE INDEX IF NOT EXISTS idx_events_timestamp ON capture_events(timestamp);
            CREATE INDEX IF NOT EXISTS idx_typed_text_start ON typed_text(start_time);
            CREATE INDEX IF NOT EXISTS idx_events_client ON capture_events(client_code);
            CREATE INDEX IF NOT EXISTS idx_events_synced ON capture_events(synced);
            CREATE INDEX IF NOT EXISTS idx_sessions_date ON work_sessions(date);
            CREATE INDEX IF NOT EXISTS idx_sessions_synced ON work_sessions(synced);
            CREATE INDEX IF NOT EXISTS idx_sync_status ON sync_queue(status);
            CREATE INDEX IF NOT EXISTS idx_screenshot_uploads_path ON screenshot_uploads(screenshot_path);
        ";
        cmd.ExecuteNonQuery();

        // Add vision columns if they don't exist (migration)
        MigrateVisionColumns(conn);

        Logger.Info("Database initialized");
    }

    private void MigrateVisionColumns(SqliteConnection conn)
    {
        // Check if vision columns exist
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(capture_events)";

        var columns = new HashSet<string>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }
        }

        // Add vision columns if missing
        var newColumns = new[]
        {
            ("vision_client_code", "TEXT"),
            ("vision_confidence", "REAL"),
            ("vision_description", "TEXT"),
            ("vision_model", "TEXT")
        };

        foreach (var (column, type) in newColumns)
        {
            if (!columns.Contains(column))
            {
                using var alterCmd = conn.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE capture_events ADD COLUMN {column} {type}";
                alterCmd.ExecuteNonQuery();
                Logger.Info($"Added column: {column}");
            }
        }
    }

    public long InsertCaptureEvent(CaptureEvent evt)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO capture_events (
                timestamp, window_title, process_name, url, hostname,
                client_code, client_confidence, capture_type, capture_reason,
                screenshot_path, image_hash, keyboard_active, mouse_active
            ) VALUES (
                @timestamp, @title, @process, @url, @hostname,
                @client, @confidence, @type, @reason,
                @path, @hash, @keyboard, @mouse
            );
            SELECT last_insert_rowid();
        ";

        cmd.Parameters.AddWithValue("@timestamp", evt.Timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@title", evt.WindowTitle ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@process", evt.ProcessName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@url", evt.Url ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@hostname", evt.Hostname ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@client", evt.ClientCode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@confidence", evt.ClientConfidence);
        cmd.Parameters.AddWithValue("@type", evt.CaptureType);
        cmd.Parameters.AddWithValue("@reason", evt.CaptureReason);
        cmd.Parameters.AddWithValue("@path", evt.ScreenshotPath ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", evt.ImageHash ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@keyboard", evt.KeyboardActive ? 1 : 0);
        cmd.Parameters.AddWithValue("@mouse", evt.MouseActive ? 1 : 0);

        return (long)cmd.ExecuteScalar()!;
    }

    public void InsertActivityBucket(WorkCapture.Detection.ActivityBucket b)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO activity_buckets (
                start_time, duration_s, keys, clicks, scrolls, mouse_moved,
                last_input_age_ms, process_name, window_title, session_state
            ) VALUES (
                @start, @dur, @keys, @clicks, @scrolls, @moved,
                @age, @process, @title, @state
            );
        ";
        cmd.Parameters.AddWithValue("@start", b.Start.ToString("O"));
        cmd.Parameters.AddWithValue("@dur", b.DurationSeconds);
        cmd.Parameters.AddWithValue("@keys", b.Keys);
        cmd.Parameters.AddWithValue("@clicks", b.Clicks);
        cmd.Parameters.AddWithValue("@scrolls", b.Scrolls);
        cmd.Parameters.AddWithValue("@moved", b.MouseMoved ? 1 : 0);
        cmd.Parameters.AddWithValue("@age", b.LastInputAgeMs);
        cmd.Parameters.AddWithValue("@process", b.ProcessName);
        cmd.Parameters.AddWithValue("@title", b.WindowTitle);
        cmd.Parameters.AddWithValue("@state", b.SessionState);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Oldest unsynced rows of activity_buckets or typed_text as JSON-ready dictionaries
    /// (column name → value), plus their ids for <see cref="MarkRowsSynced"/>.
    /// </summary>
    public (List<long> Ids, List<Dictionary<string, object?>> Rows) GetUnsyncedRows(string table, int limit)
    {
        if (table is not ("activity_buckets" or "typed_text"))
            throw new ArgumentException($"Unknown table {table}");

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM {table} WHERE synced = 0 ORDER BY id LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", limit);

        var ids = new List<long>();
        var rows = new List<Dictionary<string, object?>>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var name = reader.GetName(i);
                if (name is "synced" or "created_at") continue;
                row[name] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            ids.Add(reader.GetInt64(0));
            rows.Add(row);
        }
        return (ids, rows);
    }

    public void MarkRowsSynced(string table, IReadOnlyList<long> ids)
    {
        if (table is not ("activity_buckets" or "typed_text"))
            throw new ArgumentException($"Unknown table {table}");
        if (ids.Count == 0) return;

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"UPDATE {table} SET synced = 1 WHERE id = @id";
        var p = cmd.Parameters.Add("@id", SqliteType.Integer);
        foreach (var id in ids) { p.Value = id; cmd.ExecuteNonQuery(); }
        tx.Commit();
    }

    /// <summary>Store one sanitized typed-text entry. Accepts only the sanitizer's output type.</summary>
    public void InsertTypedText(WorkCapture.Privacy.Keystrokes.SanitizedEntry e)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO typed_text (
                start_time, end_time, process_name, window_title, text, terminator,
                redactions, suppressed, suppress_reason, key_count, shortcuts
            ) VALUES (
                @start, @end, @process, @title, @text, @terminator,
                @redactions, @suppressed, @reason, @keys, @shortcuts
            );
        ";

        cmd.Parameters.AddWithValue("@start", e.Start.ToString("O"));
        cmd.Parameters.AddWithValue("@end", e.End.ToString("O"));
        cmd.Parameters.AddWithValue("@process", e.ProcessName);
        cmd.Parameters.AddWithValue("@title", e.WindowTitle);
        cmd.Parameters.AddWithValue("@text", e.Text);
        cmd.Parameters.AddWithValue("@terminator", e.Terminator.ToString());
        cmd.Parameters.AddWithValue("@redactions", string.Join(",", e.Redactions));
        cmd.Parameters.AddWithValue("@suppressed", e.Suppressed ? 1 : 0);
        cmd.Parameters.AddWithValue("@reason", e.SuppressReason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@keys", e.KeyCount);
        cmd.Parameters.AddWithValue("@shortcuts", string.Join(",", e.Shortcuts));
        cmd.ExecuteNonQuery();
    }

    /// <summary>Client code and window title recorded for a screenshot, on any date.</summary>
    public (string? ClientCode, string? WindowTitle) GetScreenshotMetadata(string screenshotPath)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COALESCE(client_code, vision_client_code), window_title
            FROM capture_events WHERE screenshot_path = @path LIMIT 1";
        cmd.Parameters.AddWithValue("@path", screenshotPath);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return (null, null);
        return (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    public string? GetLastImageHash()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT image_hash FROM capture_events
            WHERE image_hash IS NOT NULL
            ORDER BY timestamp DESC
            LIMIT 1
        ";

        return cmd.ExecuteScalar() as string;
    }

    public List<CaptureEvent> GetEventsForDate(DateTime date)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM capture_events
            WHERE date(timestamp, 'localtime') = @date
            ORDER BY timestamp ASC
        ";
        cmd.Parameters.AddWithValue("@date", date.ToString("yyyy-MM-dd"));

        var events = new List<CaptureEvent>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            events.Add(ReadCaptureEvent(reader));
        }

        return events;
    }

    public List<CaptureEvent> GetUnsyncedEvents(int limit = 100)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM capture_events
            WHERE synced = 0
            ORDER BY timestamp ASC
            LIMIT @limit
        ";
        cmd.Parameters.AddWithValue("@limit", limit);

        var events = new List<CaptureEvent>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            events.Add(ReadCaptureEvent(reader));
        }

        return events;
    }

    public void MarkEventsSynced(IEnumerable<long> eventIds)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = $@"
            UPDATE capture_events SET synced = 1
            WHERE id IN ({string.Join(",", eventIds)})
        ";
        cmd.ExecuteNonQuery();
    }

    public DatabaseStats GetStats()
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        var stats = new DatabaseStats();
        var today = DateTime.Today.ToString("yyyy-MM-dd");

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM capture_events";
            stats.TotalEvents = Convert.ToInt32(cmd.ExecuteScalar());
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM capture_events WHERE synced = 0";
            stats.UnsyncedEvents = Convert.ToInt32(cmd.ExecuteScalar());
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM capture_events WHERE date(timestamp, 'localtime') = @date";
            cmd.Parameters.AddWithValue("@date", today);
            stats.TodayEvents = Convert.ToInt32(cmd.ExecuteScalar());
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT COALESCE(client_code, 'UNKNOWN') as client, COUNT(*) as count
                FROM capture_events
                WHERE date(timestamp, 'localtime') = @date
                GROUP BY client_code
            ";
            cmd.Parameters.AddWithValue("@date", today);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                stats.TodayByClient[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        return stats;
    }

    public void CleanupOldData(int retentionDays)
    {
        var cutoff = DateTime.Now.AddDays(-retentionDays).ToString("O");

        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM capture_events
            WHERE timestamp < @cutoff AND synced = 1;

            DELETE FROM sync_queue
            WHERE created_at < @cutoff AND status IN ('complete', 'failed');

            -- Age out regardless of synced: an upload outage must not grow these forever.
            DELETE FROM typed_text
            WHERE start_time < @cutoff;

            DELETE FROM activity_buckets
            WHERE start_time < @cutoff;
        ";
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        var deleted = cmd.ExecuteNonQuery();
        Logger.Info($"Cleaned up {deleted} old records");
    }

    public void AddToSyncQueue(string eventType, string payload)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT INTO sync_queue (event_type, payload)
            VALUES (@type, @payload)
        ";
        cmd.Parameters.AddWithValue("@type", eventType);
        cmd.Parameters.AddWithValue("@payload", payload);
        cmd.ExecuteNonQuery();
    }

    public List<SyncQueueItem> GetPendingSyncItems(int limit = 50)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT * FROM sync_queue
            WHERE status = 'pending'
            ORDER BY created_at ASC
            LIMIT @limit
        ";
        cmd.Parameters.AddWithValue("@limit", limit);

        var items = new List<SyncQueueItem>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            items.Add(new SyncQueueItem
            {
                Id = reader.GetInt64(0),
                EventType = reader.GetString(1),
                Payload = reader.GetString(2),
                CreatedAt = DateTime.Parse(reader.GetString(3)),
                Status = reader.GetString(5),
                RetryCount = reader.GetInt32(7)
            });
        }

        return items;
    }

    public void MarkSyncComplete(long id)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE sync_queue SET status = 'complete', synced_at = @now
            WHERE id = @id
        ";
        cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkSyncFailed(long id, string error)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE sync_queue SET status = 'failed', error_message = @error, retry_count = retry_count + 1
            WHERE id = @id
        ";
        cmd.Parameters.AddWithValue("@error", error);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Reset failed items back to pending so they will be retried,
    /// up to maxRetries attempts.
    /// </summary>
    public int RequeueFailedItems(int maxRetries = 5)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE sync_queue SET status = 'pending', error_message = NULL
            WHERE status = 'failed' AND retry_count < @maxRetries
        ";
        cmd.Parameters.AddWithValue("@maxRetries", maxRetries);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Update capture event with vision analysis results
    /// </summary>
    public void UpdateEventVisionData(
        long eventId,
        string? clientCode,
        double confidence,
        string? description,
        string? model)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            UPDATE capture_events SET
                vision_client_code = @clientCode,
                vision_confidence = @confidence,
                vision_description = @description,
                vision_model = @model
            WHERE id = @id
        ";
        cmd.Parameters.AddWithValue("@clientCode", clientCode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@confidence", confidence);
        cmd.Parameters.AddWithValue("@description", description ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@model", model ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@id", eventId);
        cmd.ExecuteNonQuery();
    }

    private static CaptureEvent ReadCaptureEvent(SqliteDataReader reader)
    {
        var evt = new CaptureEvent
        {
            Id = reader.GetInt64(reader.GetOrdinal("id")),
            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
            WindowTitle = GetStringOrNull(reader, "window_title"),
            ProcessName = GetStringOrNull(reader, "process_name"),
            Url = GetStringOrNull(reader, "url"),
            Hostname = GetStringOrNull(reader, "hostname"),
            ClientCode = GetStringOrNull(reader, "client_code"),
            ClientConfidence = GetDoubleOrDefault(reader, "client_confidence"),
            CaptureType = GetStringOrNull(reader, "capture_type") ?? "full",
            CaptureReason = GetStringOrNull(reader, "capture_reason") ?? "timer",
            ScreenshotPath = GetStringOrNull(reader, "screenshot_path"),
            ImageHash = GetStringOrNull(reader, "image_hash"),
            KeyboardActive = GetIntOrDefault(reader, "keyboard_active") == 1,
            MouseActive = GetIntOrDefault(reader, "mouse_active") == 1,
            Synced = GetIntOrDefault(reader, "synced") == 1,
        };

        // Read vision columns (may not exist in older databases)
        try
        {
            evt.VisionClientCode = GetStringOrNull(reader, "vision_client_code");
            evt.VisionConfidence = GetDoubleOrDefault(reader, "vision_confidence");
            evt.VisionDescription = GetStringOrNull(reader, "vision_description");
            evt.VisionModel = GetStringOrNull(reader, "vision_model");
        }
        catch { /* Vision columns may not exist yet */ }

        return evt;
    }

    private static string? GetStringOrNull(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch { return null; }
    }

    private static double GetDoubleOrDefault(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? 0 : reader.GetDouble(ordinal);
        }
        catch { return 0; }
    }

    private static int GetIntOrDefault(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? 0 : reader.GetInt32(ordinal);
        }
        catch { return 0; }
    }

    /// <summary>
    /// Check if a screenshot has been uploaded
    /// </summary>
    public bool IsScreenshotUploaded(string screenshotPath)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT COUNT(*) FROM screenshot_uploads
            WHERE screenshot_path = @path AND upload_status = 'success'
        ";
        cmd.Parameters.AddWithValue("@path", screenshotPath);

        return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
    }

    /// <summary>
    /// The file is gone (deleted by retention, or never written). Record that, so it leaves the
    /// upload queue for good. ⛔ Without this, 500 dead rows at the head of the oldest-first queue
    /// blocked every newer upload on TIER3-VM from 2026-10-18 (Kage 128699 on 10-20 billed 0).
    /// </summary>
    public void MarkScreenshotMissing(string screenshotPath)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO screenshot_uploads (screenshot_path, file_size_bytes, uploaded_at, upload_status)
            VALUES (@path, 0, @now, 'missing')";
        cmd.Parameters.AddWithValue("@path", screenshotPath);
        cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Mark a screenshot as uploaded
    /// </summary>
    public void MarkScreenshotUploaded(string screenshotPath, long fileSizeBytes, string? clientCode)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            INSERT OR REPLACE INTO screenshot_uploads (screenshot_path, file_size_bytes, client_code, uploaded_at)
            VALUES (@path, @size, @client, @now)
        ";
        cmd.Parameters.AddWithValue("@path", screenshotPath);
        cmd.Parameters.AddWithValue("@size", fileSizeBytes);
        cmd.Parameters.AddWithValue("@client", clientCode ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTime.Now.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Get unuploaded screenshots for a specific date
    /// </summary>
    /// <summary>
    /// Unuploaded screenshots, oldest first. With no date, EVERY retained day is included:
    /// until v1.10.0 only today was ever uploaded, so a backlog left at midnight was stranded.
    /// </summary>
    public List<string> GetUnuploadedScreenshots(DateTime? date = null, int limit = 500, int sinceDays = 31)
    {
        using var conn = new SqliteConnection(_connectionString);
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT screenshot_path FROM capture_events
            WHERE (@date IS NULL OR date(timestamp, 'localtime') = @date)
              AND timestamp >= @since
              AND screenshot_path IS NOT NULL
              AND screenshot_path NOT IN (
                  SELECT screenshot_path FROM screenshot_uploads
                  WHERE upload_status IN ('success', 'missing')
              )
            ORDER BY timestamp ASC
            LIMIT @limit
        ";
        cmd.Parameters.AddWithValue("@date", date?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value);
        // Files older than the agent's own retention are already deleted; never ask for them.
        cmd.Parameters.AddWithValue("@since", DateTime.Now.AddDays(-sinceDays).ToString("O"));
        cmd.Parameters.AddWithValue("@limit", limit);

        var paths = new List<string>();
        using var reader = cmd.ExecuteReader();

        while (reader.Read())
        {
            paths.Add(reader.GetString(0));
        }

        return paths;
    }

    public void Dispose()
    {
        // SQLite connections are pooled, nothing to dispose
    }
}
