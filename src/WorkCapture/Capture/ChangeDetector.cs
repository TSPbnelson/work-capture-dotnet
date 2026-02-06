namespace WorkCapture.Capture;

/// <summary>
/// Determines when to capture based on changes
/// </summary>
public class ChangeDetector
{
    private readonly int _hashThreshold;
    private readonly TimeSpan _minInterval;
    private readonly TimeSpan _maxInterval;

    private DateTime? _lastCaptureTime;
    private string? _lastWindowTitle;
    private string? _lastProcess;
    private string? _lastHash;

    public ChangeDetector(
        int hashThreshold = 5,
        int minIntervalSeconds = 2,
        int maxIntervalSeconds = 30)
    {
        _hashThreshold = hashThreshold;
        _minInterval = TimeSpan.FromSeconds(minIntervalSeconds);
        _maxInterval = TimeSpan.FromSeconds(maxIntervalSeconds);
    }

    /// <summary>
    /// Determine if a capture should occur.
    /// Triggers: first capture, max interval, window change, content change (perceptual hash).
    /// Keyboard/mouse activity is NOT a trigger - those are used only for idle detection
    /// and adaptive rate adjustment.
    /// </summary>
    /// <returns>Tuple of (shouldCapture, reason)</returns>
    public (bool ShouldCapture, string Reason) ShouldCapture(
        string? currentTitle,
        string? currentProcess,
        string? currentHash = null)
    {
        var now = DateTime.Now;

        // Always capture first time
        if (_lastCaptureTime == null)
            return (true, "first_capture");

        var timeSinceCapture = now - _lastCaptureTime.Value;

        // Enforce minimum interval
        if (timeSinceCapture < _minInterval)
            return (false, "min_interval");

        // Force capture after max interval
        if (timeSinceCapture >= _maxInterval)
            return (true, "max_interval");

        // Check for window change
        bool windowChanged = currentTitle != _lastWindowTitle || currentProcess != _lastProcess;
        if (windowChanged)
            return (true, "window_changed");

        // Check for significant screen content change via perceptual hash
        if (!string.IsNullOrEmpty(currentHash) && !string.IsNullOrEmpty(_lastHash))
        {
            var diff = ScreenCapture.HashDifference(currentHash, _lastHash);
            if (diff > _hashThreshold)
                return (true, "content_changed");
        }

        return (false, "no_change");
    }

    /// <summary>
    /// Record that a capture was made
    /// </summary>
    public void RecordCapture(string? windowTitle, string? process, string? imageHash)
    {
        _lastCaptureTime = DateTime.Now;
        _lastWindowTitle = windowTitle;
        _lastProcess = process;
        _lastHash = imageHash;
    }

    /// <summary>
    /// Set the last hash from database (for session continuity)
    /// </summary>
    public void SetLastHash(string? hash)
    {
        _lastHash = hash;
    }

    /// <summary>
    /// Get time since last capture
    /// </summary>
    public TimeSpan? TimeSinceLastCapture =>
        _lastCaptureTime.HasValue ? DateTime.Now - _lastCaptureTime.Value : null;

    /// <summary>
    /// Reset detector state
    /// </summary>
    public void Reset()
    {
        _lastCaptureTime = null;
        _lastWindowTitle = null;
        _lastProcess = null;
        _lastHash = null;
    }
}

/// <summary>
/// Adjusts capture rate based on activity level
/// </summary>
public class AdaptiveCaptureRate
{
    private readonly double _baseInterval;
    private readonly double _minInterval;
    private readonly double _maxInterval;

    private double _currentInterval;
    private double _activityLevel = 0.5;

    public AdaptiveCaptureRate(
        double baseInterval = 5.0,
        double minInterval = 2.0,
        double maxInterval = 30.0)
    {
        _baseInterval = baseInterval;
        _minInterval = minInterval;
        _maxInterval = maxInterval;
        _currentInterval = baseInterval;
    }

    /// <summary>
    /// Update activity level based on recent activity
    /// </summary>
    public void UpdateActivity(bool keyboardActive, bool mouseActive)
    {
        double increment = 0.0;

        if (keyboardActive)
            increment += 0.3;
        if (mouseActive)
            increment += 0.1;

        // Decay toward baseline
        _activityLevel = _activityLevel * 0.9 + increment;

        // Clamp to [0, 1]
        _activityLevel = Math.Clamp(_activityLevel, 0.0, 1.0);

        // Calculate interval: high activity = short interval
        var range = _maxInterval - _minInterval;
        _currentInterval = _maxInterval - (_activityLevel * range);
    }

    /// <summary>
    /// Get current recommended capture interval in seconds
    /// </summary>
    public double CurrentInterval => _currentInterval;

    /// <summary>
    /// Get current activity level (0-1)
    /// </summary>
    public double ActivityLevel => _activityLevel;
}
