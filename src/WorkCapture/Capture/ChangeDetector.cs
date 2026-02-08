namespace WorkCapture.Capture;

/// <summary>
/// Pure change detection: only saves when screen content actually changes.
/// Used with a fixed-interval timer (from settings). The timer wakes every N seconds,
/// captures to memory, compares perceptual hash, and only saves if changed.
/// Max interval (1 hour) provides a safety backup capture on completely static screens.
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
    /// Determine if a capture should be saved.
    /// Triggers: first capture, max interval (1hr safety), window change, content change (perceptual hash).
    /// Keyboard/mouse activity is NOT a trigger - used only for idle detection and metadata.
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
