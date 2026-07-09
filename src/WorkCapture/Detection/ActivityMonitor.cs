using System.Runtime.InteropServices;
using SharpHook;
using SharpHook.Native;

namespace WorkCapture.Detection;

/// <summary>
/// Monitors keyboard and mouse activity using global hooks
/// </summary>
public class ActivityMonitor : IDisposable
{
    private readonly TimeSpan _activityWindow;
    private readonly SimpleGlobalHook _hook;

    private DateTime? _lastKeyboardTime;
    private DateTime? _lastMouseTime;
    private int _keyboardCount;
    private int _mouseClickCount;
    private readonly object _lock = new();
    private readonly DateTime _startTime = DateTime.Now;

    private bool _running;

    public event Action<string>? OnActivity;

    public ActivityMonitor(int activityWindowMs = 500)
    {
        _activityWindow = TimeSpan.FromMilliseconds(activityWindowMs);
        _hook = new SimpleGlobalHook();

        _hook.KeyPressed += OnKeyPressed;
        _hook.MouseClicked += OnMouseClicked;
        _hook.MouseMoved += OnMouseMoved;
    }

    /// <summary>
    /// Start monitoring activity
    /// </summary>
    public void Start()
    {
        if (_running) return;

        _running = true;

        // Run hook on background thread
        Task.Run(() =>
        {
            try
            {
                _hook.Run();
            }
            catch (Exception ex)
            {
                Logger.Error($"Activity hook error: {ex.Message}");
            }
        });

        Logger.Info("Activity monitoring started");
    }

    /// <summary>
    /// Stop monitoring activity
    /// </summary>
    public void Stop()
    {
        _running = false;

        try
        {
            _hook.Dispose();
        }
        catch
        {
            // Ignore disposal errors
        }

        Logger.Info("Activity monitoring stopped");
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e)
    {
        lock (_lock)
        {
            _lastKeyboardTime = DateTime.Now;
            _keyboardCount++;
        }

        OnActivity?.Invoke("keyboard");
    }

    private void OnMouseClicked(object? sender, MouseHookEventArgs e)
    {
        lock (_lock)
        {
            _lastMouseTime = DateTime.Now;
            _mouseClickCount++;
        }

        OnActivity?.Invoke("mouse_click");
    }

    private void OnMouseMoved(object? sender, MouseHookEventArgs e)
    {
        lock (_lock)
        {
            _lastMouseTime = DateTime.Now;
        }
    }

    /// <summary>
    /// Check if keyboard was recently active
    /// </summary>
    public bool IsKeyboardActive
    {
        get
        {
            lock (_lock)
            {
                if (_lastKeyboardTime == null) return false;
                return (DateTime.Now - _lastKeyboardTime.Value) <= _activityWindow;
            }
        }
    }

    /// <summary>
    /// Check if mouse was recently active
    /// </summary>
    public bool IsMouseActive
    {
        get
        {
            lock (_lock)
            {
                if (_lastMouseTime == null) return false;
                return (DateTime.Now - _lastMouseTime.Value) <= _activityWindow;
            }
        }
    }

    /// <summary>
    /// Check if any activity is recent
    /// </summary>
    public bool IsActive => IsKeyboardActive || IsMouseActive;

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    /// <summary>
    /// Get seconds since the last keyboard/mouse input.
    ///
    /// Uses the Win32 GetLastInputInfo API, which per Microsoft's docs reports
    /// "session-specific user input for the session in which the calling thread is running."
    /// This is reliable inside RDP sessions and VMs — unlike the SharpHook global hooks, which
    /// frequently fail to register events over RDP and would make the agent think an actively
    /// working user is idle (silently stopping capture). GetLastInputInfo is what the input-
    /// activity capture model depends on.
    ///
    /// Falls back to the hook-based timestamps only if the API call fails.
    /// </summary>
    public double IdleSeconds
    {
        get
        {
            var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref lii))
            {
                // Unsigned subtraction handles GetTickCount wraparound (~24.9 days) correctly.
                uint idleMs = unchecked((uint)Environment.TickCount) - lii.dwTime;
                return idleMs / 1000.0;
            }

            lock (_lock)
            {
                var times = new[] { _lastKeyboardTime, _lastMouseTime }
                    .Where(t => t.HasValue)
                    .Select(t => t!.Value)
                    .ToList();

                if (!times.Any())
                    return (DateTime.Now - _startTime).TotalSeconds;

                return (DateTime.Now - times.Max()).TotalSeconds;
            }
        }
    }

    /// <summary>
    /// Get activity statistics
    /// </summary>
    public ActivityStats GetStats(bool reset = false)
    {
        lock (_lock)
        {
            var stats = new ActivityStats
            {
                KeyboardActive = IsKeyboardActive,
                MouseActive = IsMouseActive,
                KeyboardCount = _keyboardCount,
                MouseClickCount = _mouseClickCount,
                LastKeyboardTime = _lastKeyboardTime,
                LastMouseTime = _lastMouseTime
            };

            if (reset)
            {
                _keyboardCount = 0;
                _mouseClickCount = 0;
            }

            return stats;
        }
    }

    public void Dispose()
    {
        Stop();
    }
}

/// <summary>
/// Activity statistics
/// </summary>
public class ActivityStats
{
    public bool KeyboardActive { get; set; }
    public bool MouseActive { get; set; }
    public int KeyboardCount { get; set; }
    public int MouseClickCount { get; set; }
    public DateTime? LastKeyboardTime { get; set; }
    public DateTime? LastMouseTime { get; set; }
}
