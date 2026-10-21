using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using SharpHook;
using WorkCapture.Data;
using WorkCapture.Privacy.Redaction;

namespace WorkCapture.Detection;

/// <summary>
/// The activity stream: one row per 5-second bucket with input counts, the foreground app and
/// window, and the session state. This, not the screenshot cadence, is what minutes will be
/// computed from (server Phase 2), so it is recorded regardless of idle, privacy exclusions or
/// app filters: a password manager or a blocked app still shows as active time.
///
/// Two independent input signals, because global hooks are known to miss input over RDP:
///   - hook counters (keys / clicks / scrolls / mouse moved), incremented in the hook callback
///   - GetLastInputInfo age at bucket close (session-aware, reliable over RDP)
/// The server treats a bucket as active if either shows input.
///
/// Session state is polled every bucket (WTS connect state: connected / disconnected) and
/// lock/unlock comes from SystemEvents.SessionSwitch, so a disconnected or locked session can be
/// billed as zero even if stale input timestamps say otherwise.
///
/// Consecutive idle buckets with the same window and state are merged into one row with a
/// longer duration, so an idle night is a handful of rows, not 17,000.
/// </summary>
public sealed class ActivityRecorder : IDisposable
{
    public const int BucketSeconds = 5;
    private static readonly TimeSpan MaxIdleRow = TimeSpan.FromMinutes(5);

    private readonly Database _db;
    private readonly Func<double> _idleSeconds;
    private readonly SecretRedactor _titleRedactor = new();

    private IGlobalHook? _hook;
    private System.Threading.Timer? _timer;
    private readonly object _tickLock = new();

    private int _keys, _clicks, _scrolls, _moved;
    private volatile bool _locked;
    private DateTime _bucketStart;
    private ActivityBucket? _pendingIdle;

    public long BucketsWritten;
    public long ActiveBuckets;

    public ActivityRecorder(Database db, Func<double> idleSeconds)
    {
        _db = db;
        _idleSeconds = idleSeconds;
    }

    public void Attach(IGlobalHook hook)
    {
        _hook = hook;
        hook.KeyPressed += OnKey;
        hook.MousePressed += OnClick;
        hook.MouseWheel += OnWheel;
        hook.MouseMoved += OnMove;
    }

    public void Start()
    {
        if (_timer != null) return;
        // Discard anything the hook counted while we were stopped/paused.
        Interlocked.Exchange(ref _keys, 0); Interlocked.Exchange(ref _clicks, 0);
        Interlocked.Exchange(ref _scrolls, 0); Interlocked.Exchange(ref _moved, 0);

        try { SystemEvents.SessionSwitch += OnSessionSwitch; }
        catch (Exception ex) { Logger.Warning($"SessionSwitch unavailable: {ex.Message}"); }

        // Align buckets to wall-clock 5 s boundaries so buckets from every VM line up.
        var now = DateTime.Now;
        _bucketStart = now.AddTicks(-(now.Ticks % TimeSpan.FromSeconds(BucketSeconds).Ticks));
        var firstDue = _bucketStart.AddSeconds(BucketSeconds) - now;
        _timer = new System.Threading.Timer(_ => Tick(), null, firstDue, TimeSpan.FromSeconds(BucketSeconds));
        Logger.Info("Activity recorder started (5 s buckets)");
    }

    public void Stop()
    {
        if (_timer == null) return;   // not started, or already stopped
        _timer.Dispose();
        _timer = null;
        lock (_tickLock)
        {
            try { CloseBucket(DateTime.Now); FlushIdle(); }
            catch (Exception ex) { Logger.Warning($"Activity flush on stop failed: {ex.Message}"); }
        }
        try { SystemEvents.SessionSwitch -= OnSessionSwitch; } catch { }
        Logger.Info($"Activity recorder stopped | {StatusText()}");
    }

    public string StatusText() =>
        $"buckets={Interlocked.Read(ref BucketsWritten)} active={Interlocked.Read(ref ActiveBuckets)} " +
        $"state={CurrentState()}";

    // ---- hook thread: counters only ---------------------------------------------------------

    private void OnKey(object? s, KeyboardHookEventArgs e) => Interlocked.Increment(ref _keys);
    private void OnClick(object? s, MouseHookEventArgs e) => Interlocked.Increment(ref _clicks);
    private void OnWheel(object? s, MouseWheelHookEventArgs e) => Interlocked.Increment(ref _scrolls);
    private void OnMove(object? s, MouseHookEventArgs e) => Interlocked.Exchange(ref _moved, 1);

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock: _locked = true; break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.SessionLogon: _locked = false; break;
        }
        Logger.Info($"Session switch: {e.Reason}");
    }

    // ---- timer thread ------------------------------------------------------------------------

    private void Tick()
    {
        if (!Monitor.TryEnter(_tickLock)) return;   // a slow DB write must not stack ticks
        try { CloseBucket(DateTime.Now); }
        catch (Exception ex) { Logger.Warning($"Activity bucket failed: {ex.GetType().Name}: {ex.Message}"); }
        finally { Monitor.Exit(_tickLock); }
    }

    private void CloseBucket(DateTime now)
    {
        var start = _bucketStart;
        var seconds = (int)Math.Round((now - start).TotalSeconds);
        if (seconds <= 0) return;
        _bucketStart = now;

        var (process, title) = Foreground();
        var b = new ActivityBucket
        {
            Start = start,
            DurationSeconds = seconds,
            Keys = Interlocked.Exchange(ref _keys, 0),
            Clicks = Interlocked.Exchange(ref _clicks, 0),
            Scrolls = Interlocked.Exchange(ref _scrolls, 0),
            MouseMoved = Interlocked.Exchange(ref _moved, 0) == 1,
            LastInputAgeMs = (long)(_idleSeconds() * 1000),
            ProcessName = process,
            WindowTitle = _titleRedactor.Redact(title, RedactionMode.General).Text,
            SessionState = CurrentState(),
        };

        bool active = b.Keys + b.Clicks + b.Scrolls > 0 || b.MouseMoved || b.LastInputAgeMs < seconds * 1000L;

        if (active)
        {
            FlushIdle();
            Write(b);
            Interlocked.Increment(ref ActiveBuckets);
            return;
        }

        // Idle: extend the pending idle row while nothing about the context changes.
        if (_pendingIdle != null
            && _pendingIdle.ProcessName == b.ProcessName
            && _pendingIdle.WindowTitle == b.WindowTitle
            && _pendingIdle.SessionState == b.SessionState
            && _pendingIdle.DurationSeconds + seconds <= MaxIdleRow.TotalSeconds)
        {
            _pendingIdle.DurationSeconds += seconds;
            _pendingIdle.LastInputAgeMs = b.LastInputAgeMs;
            return;
        }

        FlushIdle();
        _pendingIdle = b;
    }

    private void FlushIdle()
    {
        if (_pendingIdle == null) return;
        Write(_pendingIdle);
        _pendingIdle = null;
    }

    private void Write(ActivityBucket b)
    {
        _db.InsertActivityBucket(b);
        Interlocked.Increment(ref BucketsWritten);
    }

    private string CurrentState()
    {
        if (!IsSessionConnected()) return "disconnected";
        return _locked ? "locked" : "active";
    }

    // ---- Win32 -------------------------------------------------------------------------------

    private static (string Process, string Title) Foreground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return ("", "");
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        string name;
        try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
        catch { name = ""; }
        return (name, sb.ToString());
    }

    /// <summary>WTS connect state of this process's session. Unknown counts as connected.</summary>
    private static bool IsSessionConnected()
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, WTS_CURRENT_SESSION, WTSConnectState, out var buf, out _))
            return true;
        try
        {
            var state = Marshal.ReadInt32(buf);
            return state is WTSActive or WTSConnected;
        }
        finally { WTSFreeMemory(buf); }
    }

    private const int WTS_CURRENT_SESSION = -1;
    private const int WTSConnectState = 8;
    private const int WTSActive = 0, WTSConnected = 1;

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);

    public void Dispose()
    {
        Stop();
        if (_hook != null)
        {
            _hook.KeyPressed -= OnKey;
            _hook.MousePressed -= OnClick;
            _hook.MouseWheel -= OnWheel;
            _hook.MouseMoved -= OnMove;
        }
    }
}

/// <summary>One activity-stream row (5 s, or longer for merged idle stretches).</summary>
public class ActivityBucket
{
    public DateTime Start { get; set; }
    public int DurationSeconds { get; set; }
    public int Keys { get; set; }
    public int Clicks { get; set; }
    public int Scrolls { get; set; }
    public bool MouseMoved { get; set; }
    public long LastInputAgeMs { get; set; }
    public string ProcessName { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string SessionState { get; set; } = "active";
}
