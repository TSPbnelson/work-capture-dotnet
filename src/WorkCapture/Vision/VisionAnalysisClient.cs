using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkCapture.Config;

namespace WorkCapture.Vision;

/// <summary>
/// Client for the Vision Analysis Service
/// Handles screenshot analysis via Ollama/Claude vision models
/// </summary>
public class VisionAnalysisClient : IDisposable
{
    private readonly HttpClient _client;
    private readonly VisionSettings _settings;
    private readonly JsonSerializerOptions _jsonOptions;

    // Statistics
    private int _totalRequests;
    private int _successfulRequests;
    private int _failedRequests;
    private long _totalTimeMs;

    public VisionAnalysisClient(VisionSettings settings)
    {
        _settings = settings;

        _client = new HttpClient
        {
            BaseAddress = new Uri(settings.ServiceUrl),
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        };

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    /// <summary>
    /// Check if the vision service is enabled
    /// </summary>
    public bool IsEnabled => _settings.Enabled;

    /// <summary>
    /// Analyze a screenshot file
    /// </summary>
    /// <param name="screenshotPath">Path to the screenshot file</param>
    /// <param name="windowTitle">Active window title</param>
    /// <param name="processName">Active process name</param>
    /// <param name="hostname">Source hostname</param>
    /// <param name="url">URL if browser window</param>
    /// <returns>Vision analysis result</returns>
    public async Task<VisionAnalysisResult> AnalyzeAsync(
        string screenshotPath,
        string? windowTitle = null,
        string? processName = null,
        string? hostname = null,
        string? url = null)
    {
        if (!_settings.Enabled)
        {
            return new VisionAnalysisResult
            {
                Success = false,
                Error = "Vision analysis is disabled"
            };
        }

        if (!File.Exists(screenshotPath))
        {
            return new VisionAnalysisResult
            {
                Success = false,
                Error = $"Screenshot file not found: {screenshotPath}"
            };
        }

        _totalRequests++;
        var startTime = DateTime.Now;

        try
        {
            // Read and encode the screenshot
            var imageBytes = await File.ReadAllBytesAsync(screenshotPath);
            var imageBase64 = Convert.ToBase64String(imageBytes);

            // Determine media type from extension
            var extension = Path.GetExtension(screenshotPath).ToLowerInvariant();
            var mediaType = extension switch
            {
                ".webp" => "image/webp",
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "image/png"
            };

            // Create request
            var request = new VisionAnalyzeRequest
            {
                ScreenshotBase64 = imageBase64,
                WindowTitle = windowTitle,
                ProcessName = processName,
                Hostname = hostname,
                Url = url,
                MediaType = mediaType,
                ForceClaude = false
            };

            // Send to vision service
            var response = await _client.PostAsJsonAsync("/api/vision/analyze", request, _jsonOptions);

            var elapsed = (long)(DateTime.Now - startTime).TotalMilliseconds;
            _totalTimeMs += elapsed;

            if (!response.IsSuccessStatusCode)
            {
                var errorContent = await response.Content.ReadAsStringAsync();
                Logger.Warning($"Vision service error: {response.StatusCode} - {errorContent}");

                _failedRequests++;
                return new VisionAnalysisResult
                {
                    Success = false,
                    Error = $"HTTP {(int)response.StatusCode}: {errorContent}",
                    ResponseTimeMs = (int)elapsed
                };
            }

            var result = await response.Content.ReadFromJsonAsync<VisionAnalysisResult>(_jsonOptions);

            if (result == null)
            {
                _failedRequests++;
                return new VisionAnalysisResult
                {
                    Success = false,
                    Error = "Empty response from vision service",
                    ResponseTimeMs = (int)elapsed
                };
            }

            result.ResponseTimeMs = (int)elapsed;

            if (result.Success)
            {
                _successfulRequests++;
                Logger.Debug($"Vision analysis: client={result.ClientCode}, " +
                             $"confidence={result.Confidence:F2}, " +
                             $"model={result.Model}, time={elapsed}ms");
            }
            else
            {
                _failedRequests++;
                Logger.Warning($"Vision analysis failed: {result.Error}");
            }

            return result;
        }
        catch (TaskCanceledException)
        {
            var elapsed = (long)(DateTime.Now - startTime).TotalMilliseconds;
            _totalTimeMs += elapsed;
            _failedRequests++;

            Logger.Warning($"Vision service timeout after {_settings.TimeoutSeconds}s");

            return new VisionAnalysisResult
            {
                Success = false,
                Error = $"Request timed out after {_settings.TimeoutSeconds}s",
                ResponseTimeMs = (int)elapsed
            };
        }
        catch (HttpRequestException ex)
        {
            var elapsed = (long)(DateTime.Now - startTime).TotalMilliseconds;
            _totalTimeMs += elapsed;
            _failedRequests++;

            Logger.Error($"Vision service connection error: {ex.Message}");

            return new VisionAnalysisResult
            {
                Success = false,
                Error = $"Connection error: {ex.Message}",
                ResponseTimeMs = (int)elapsed
            };
        }
        catch (Exception ex)
        {
            var elapsed = (long)(DateTime.Now - startTime).TotalMilliseconds;
            _totalTimeMs += elapsed;
            _failedRequests++;

            Logger.Error($"Vision analysis error: {ex.Message}");

            return new VisionAnalysisResult
            {
                Success = false,
                Error = ex.Message,
                ResponseTimeMs = (int)elapsed
            };
        }
    }

    /// <summary>
    /// Check if the vision service is healthy
    /// </summary>
    public async Task<bool> IsHealthyAsync()
    {
        try
        {
            var response = await _client.GetAsync("/api/vision/health");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Get client statistics
    /// </summary>
    public VisionClientStats GetStats()
    {
        var avgTime = _totalRequests > 0 ? _totalTimeMs / _totalRequests : 0;
        var successRate = _totalRequests > 0 ? (double)_successfulRequests / _totalRequests * 100 : 0;

        return new VisionClientStats
        {
            TotalRequests = _totalRequests,
            SuccessfulRequests = _successfulRequests,
            FailedRequests = _failedRequests,
            AverageTimeMs = avgTime,
            SuccessRatePercent = successRate
        };
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

/// <summary>
/// Request to the vision analysis service
/// </summary>
public class VisionAnalyzeRequest
{
    [JsonPropertyName("screenshot_base64")]
    public string ScreenshotBase64 { get; set; } = "";

    [JsonPropertyName("window_title")]
    public string? WindowTitle { get; set; }

    [JsonPropertyName("process_name")]
    public string? ProcessName { get; set; }

    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "image/png";

    [JsonPropertyName("force_claude")]
    public bool ForceClaude { get; set; }
}

/// <summary>
/// Result from the vision analysis service
/// </summary>
public class VisionAnalysisResult
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("client_code")]
    public string? ClientCode { get; set; }

    [JsonPropertyName("confidence")]
    public double Confidence { get; set; }

    [JsonPropertyName("work_description")]
    public string WorkDescription { get; set; } = "";

    [JsonPropertyName("step_summary")]
    public string StepSummary { get; set; } = "";

    [JsonPropertyName("activity_type")]
    public string ActivityType { get; set; } = "administrative";

    [JsonPropertyName("indicators_found")]
    public List<string> IndicatorsFound { get; set; } = new();

    [JsonPropertyName("model")]
    public string Model { get; set; } = "";

    [JsonPropertyName("response_time_ms")]
    public int ResponseTimeMs { get; set; }

    [JsonPropertyName("escalated")]
    public bool Escalated { get; set; }

    [JsonPropertyName("routing")]
    public string Routing { get; set; } = "";

    [JsonPropertyName("error")]
    public string? Error { get; set; }
}

/// <summary>
/// Vision client statistics
/// </summary>
public class VisionClientStats
{
    public int TotalRequests { get; set; }
    public int SuccessfulRequests { get; set; }
    public int FailedRequests { get; set; }
    public long AverageTimeMs { get; set; }
    public double SuccessRatePercent { get; set; }
}
