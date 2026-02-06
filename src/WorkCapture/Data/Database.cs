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

            CREATE INDEX IF NOT EXISTS idx_events_timestamp ON capture_events(timestamp);
            CREATE INDEX IF NOT EXISTS idx_events_client ON capture_events(client_code);
            CREATE INDEX IF NOT EXISTS idx_events_synced ON capture_events(synced);
            CREATE INDEX IF NOT EXISTS idx_sessions_date ON work_sessions(date);
            CREATE INDEX IF NOT EXISTS idx_sessions_synced ON work_sessions(synced);
            CREATE INDEX IF NOT EXISTS idx_sync_status ON sync_queue(status);
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
            WHERE date(timestamp) = @date
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
            cmd.CommandText = "SELECT COUNT(*) FROM capture_events WHERE date(timestamp) = @date";
            cmd.Parameters.AddWithValue("@date", today);
            stats.TodayEvents = Convert.ToInt32(cmd.ExecuteScalar());
        }

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT COALESCE(client_code, 'UNKNOWN') as client, COUNT(*) as count
                FROM capture_events
                WHERE date(timestamp) = @date
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

    public void Dispose()
    {
        // SQLite connections are pooled, nothing to dispose
    }
}
