namespace WorkCapture.Vision;

/// <summary>
/// Accumulates step summaries from vision analysis to build work descriptions
/// </summary>
public class StepSummaryAccumulator
{
    private readonly List<StepEntry> _steps = new();
    private readonly object _lock = new();
    private readonly int _maxSteps;

    public StepSummaryAccumulator(int maxSteps = 100)
    {
        _maxSteps = maxSteps;
    }

    /// <summary>
    /// Add a step from vision analysis
    /// </summary>
    public void AddStep(string? clientCode, string stepSummary, string activityType, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(stepSummary))
            return;

        lock (_lock)
        {
            _steps.Add(new StepEntry
            {
                ClientCode = clientCode ?? "UNKNOWN",
                Summary = stepSummary,
                ActivityType = activityType,
                Timestamp = timestamp
            });

            // Trim if over max
            while (_steps.Count > _maxSteps)
            {
                _steps.RemoveAt(0);
            }
        }
    }

    /// <summary>
    /// Get work description for a client on a specific date
    /// </summary>
    public string GetWorkDescription(string clientCode, DateTime date)
    {
        lock (_lock)
        {
            var clientSteps = _steps
                .Where(s => s.ClientCode == clientCode &&
                           s.Timestamp.Date == date.Date)
                .OrderBy(s => s.Timestamp)
                .ToList();

            if (!clientSteps.Any())
                return "";

            // Group by activity type
            var byActivity = clientSteps
                .GroupBy(s => s.ActivityType)
                .OrderByDescending(g => g.Count())
                .ToList();

            // Build description
            var parts = new List<string>();

            foreach (var group in byActivity)
            {
                var activityLabel = FormatActivityType(group.Key);
                var uniqueSummaries = group
                    .Select(s => s.Summary)
                    .Distinct()
                    .Take(5)
                    .ToList();

                if (uniqueSummaries.Count == 1)
                {
                    parts.Add($"{activityLabel}: {uniqueSummaries[0]}");
                }
                else
                {
                    parts.Add($"{activityLabel}: {string.Join("; ", uniqueSummaries)}");
                }
            }

            return string.Join(". ", parts);
        }
    }

    /// <summary>
    /// Get all unique step summaries for a client/date
    /// </summary>
    public List<string> GetStepSummaries(string clientCode, DateTime date)
    {
        lock (_lock)
        {
            return _steps
                .Where(s => s.ClientCode == clientCode &&
                           s.Timestamp.Date == date.Date)
                .Select(s => s.Summary)
                .Distinct()
                .ToList();
        }
    }

    /// <summary>
    /// Get duration breakdown by activity type for a client/date
    /// </summary>
    public Dictionary<string, int> GetActivityBreakdown(string clientCode, DateTime date)
    {
        lock (_lock)
        {
            return _steps
                .Where(s => s.ClientCode == clientCode &&
                           s.Timestamp.Date == date.Date)
                .GroupBy(s => s.ActivityType)
                .ToDictionary(g => g.Key, g => g.Count());
        }
    }

    /// <summary>
    /// Clear accumulated steps
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _steps.Clear();
        }
    }

    /// <summary>
    /// Clear steps older than a certain date
    /// </summary>
    public void ClearOlderThan(DateTime date)
    {
        lock (_lock)
        {
            _steps.RemoveAll(s => s.Timestamp.Date < date.Date);
        }
    }

    /// <summary>
    /// Get summary statistics
    /// </summary>
    public StepAccumulatorStats GetStats()
    {
        lock (_lock)
        {
            var clientCounts = _steps
                .GroupBy(s => s.ClientCode)
                .ToDictionary(g => g.Key, g => g.Count());

            return new StepAccumulatorStats
            {
                TotalSteps = _steps.Count,
                UniqueClients = clientCounts.Keys.Count,
                StepsByClient = clientCounts,
                OldestStep = _steps.FirstOrDefault()?.Timestamp,
                NewestStep = _steps.LastOrDefault()?.Timestamp
            };
        }
    }

    private static string FormatActivityType(string activityType)
    {
        return activityType switch
        {
            "coding" => "Development",
            "documentation" => "Documentation",
            "configuration" => "Configuration",
            "troubleshooting" => "Troubleshooting",
            "communication" => "Communication",
            "meeting" => "Meeting",
            "research" => "Research",
            "administrative" => "Administrative",
            "monitoring" => "Monitoring",
            "security" => "Security",
            _ => activityType
        };
    }
}

/// <summary>
/// A single step entry
/// </summary>
internal class StepEntry
{
    public string ClientCode { get; set; } = "";
    public string Summary { get; set; } = "";
    public string ActivityType { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Step accumulator statistics
/// </summary>
public class StepAccumulatorStats
{
    public int TotalSteps { get; set; }
    public int UniqueClients { get; set; }
    public Dictionary<string, int> StepsByClient { get; set; } = new();
    public DateTime? OldestStep { get; set; }
    public DateTime? NewestStep { get; set; }
}
