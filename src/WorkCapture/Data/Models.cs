namespace WorkCapture.Data;

/// <summary>
/// A single capture event
/// </summary>
public class CaptureEvent
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string? WindowTitle { get; set; }
    public string? ProcessName { get; set; }
    public string? Url { get; set; }
    public string? Hostname { get; set; }
    public string? ClientCode { get; set; }
    public double ClientConfidence { get; set; }
    public string CaptureType { get; set; } = "full"; // full, metadata_only, skipped
    public string CaptureReason { get; set; } = "timer"; // timer, window_change, keyboard_active
    public string? ScreenshotPath { get; set; }
    public string? ImageHash { get; set; }
    public bool KeyboardActive { get; set; }
    public bool MouseActive { get; set; }
    public bool Synced { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Aggregated work session
/// </summary>
public class WorkSession
{
    public long Id { get; set; }
    public string? ClientCode { get; set; }
    public DateTime Date { get; set; }
    public DateTime StartTime { get; set; }
    public DateTime EndTime { get; set; }
    public int DurationMinutes { get; set; }
    public int CaptureCount { get; set; }
    public string? WorkDescription { get; set; }
    public bool Synced { get; set; }
    public string? SyncId { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Item in the sync queue
/// </summary>
public class SyncQueueItem
{
    public long Id { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime? SyncedAt { get; set; }
    public string Status { get; set; } = "pending";
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
}

/// <summary>
/// Information about the active window
/// </summary>
public class WindowInfo
{
    public string Title { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public int ProcessId { get; set; }
    public string? Url { get; set; }
    public string? Hostname { get; set; }
    public bool IsBrowser { get; set; }
    public bool IsTerminal { get; set; }
    public bool IsRemoteDesktop { get; set; }
}

/// <summary>
/// Result of client detection
/// </summary>
public class ClientMatch
{
    public string ClientName { get; set; } = "";
    public string ClientCode { get; set; } = "";
    public double Confidence { get; set; }
    public string MatchedRule { get; set; } = "";
    public string MatchedValue { get; set; } = "";
}

/// <summary>
/// Database statistics
/// </summary>
public class DatabaseStats
{
    public int TotalEvents { get; set; }
    public int UnsyncedEvents { get; set; }
    public int TodayEvents { get; set; }
    public Dictionary<string, int> TodayByClient { get; set; } = new();
}
