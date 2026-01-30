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

    /// <summary>
    /// Get seconds since last activity
    /// </summary>
    public double IdleSeconds
    {
        get
        {
            lock (_lock)
            {
                var times = new[] { _lastKeyboardTime, _lastMouseTime }
                    .Where(t => t.HasValue)
                    .Select(t => t!.Value)
                    .ToList();

                if (!times.Any())
                    return double.MaxValue;

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
