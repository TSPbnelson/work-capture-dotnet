using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace WorkCapture.Updater;

/// <summary>
/// Self-updater that pulls latest release from GitHub
/// </summary>
public class AppUpdater : IDisposable
{
    private const string RepoOwner = "TSPbnelson";
    private const string RepoName = "work-capture-dotnet";
    private const string InstallDir = @"C:\WorkCapture";

    private readonly HttpClient _client;
    private readonly string _currentVersion;

    public AppUpdater()
    {
        _client = new HttpClient();
        _client.DefaultRequestHeaders.Add("User-Agent", "WorkCapture-Updater");

        // Get current version from assembly
        var asm = typeof(AppUpdater).Assembly.GetName();
        _currentVersion = asm.Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Check GitHub for latest release version
    /// </summary>
    public async Task<ReleaseInfo?> CheckForUpdate()
    {
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var release = await _client.GetFromJsonAsync<GitHubRelease>(url);

            if (release == null) return null;

            var latestVersion = release.TagName.TrimStart('v');

            var current = Version.Parse(_currentVersion);
            var latest = Version.Parse(latestVersion);

            // Find the win-x64 ZIP asset
            var asset = release.Assets?.FirstOrDefault(a =>
                a.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));

            return new ReleaseInfo
            {
                CurrentVersion = _currentVersion,
                LatestVersion = latestVersion,
                UpdateAvailable = latest > current,
                DownloadUrl = asset?.BrowserDownloadUrl ?? "",
                ReleaseNotes = release.Body ?? "",
                AssetName = asset?.Name ?? ""
            };
        }
        catch (Exception ex)
        {
            Logger.Error($"Update check failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Download and install the update, then restart
    /// </summary>
    public async Task<bool> DownloadAndInstall(ReleaseInfo release, Action<string>? onProgress = null)
    {
        if (string.IsNullOrEmpty(release.DownloadUrl))
        {
            Logger.Error("No download URL for update");
            return false;
        }

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "WorkCapture_Update");
            var zipPath = Path.Combine(tempDir, release.AssetName);

            // Clean temp dir
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
            Directory.CreateDirectory(tempDir);

            // Download ZIP
            onProgress?.Invoke("Downloading update...");
            Logger.Info($"Downloading {release.DownloadUrl}");

            using (var response = await _client.GetAsync(release.DownloadUrl))
            {
                response.EnsureSuccessStatusCode();
                using var fs = File.Create(zipPath);
                await response.Content.CopyToAsync(fs);
            }

            onProgress?.Invoke("Download complete. Preparing update...");
            Logger.Info($"Downloaded to {zipPath}");

            // Extract to temp
            var extractDir = Path.Combine(tempDir, "extracted");
            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            // Write update batch script
            // This script waits for the app to exit, copies files, then relaunches
            var batchPath = Path.Combine(tempDir, "update.cmd");
            var exePath = Path.Combine(InstallDir, "WorkCapture.exe");
            var currentPid = Environment.ProcessId;

            var batch = $"""
                @echo off
                echo Waiting for WorkCapture to exit...
                :waitloop
                tasklist /FI "PID eq {currentPid}" 2>NUL | find /I "{currentPid}" >NUL
                if not errorlevel 1 (
                    timeout /t 1 /nobreak >NUL
                    goto waitloop
                )
                echo Updating WorkCapture to v{release.LatestVersion}...
                xcopy /Y /E /I "{extractDir}\*" "{InstallDir}\"
                echo Update complete. Starting WorkCapture...
                start "" "{exePath}"
                del "%~f0"
                """;

            File.WriteAllText(batchPath, batch);

            onProgress?.Invoke($"Installing v{release.LatestVersion}. App will restart...");
            Logger.Info("Launching update script and exiting");

            // Launch the batch script hidden
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{batchPath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);

            return true; // Caller should exit the app
        }
        catch (Exception ex)
        {
            Logger.Error($"Update failed: {ex.Message}");
            onProgress?.Invoke($"Update failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Register app in Windows startup (HKCU, no admin needed)
    /// </summary>
    public static void RegisterStartup()
    {
        try
        {
            var exePath = Path.Combine(InstallDir, "WorkCapture.exe");
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.SetValue("WorkCapture", $"\"{exePath}\"");
            Logger.Info("Registered in Windows startup");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to register startup: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove app from Windows startup
    /// </summary>
    public static void UnregisterStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("WorkCapture", false);
            Logger.Info("Removed from Windows startup");
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to unregister startup: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if registered in startup
    /// </summary>
    public static bool IsRegisteredForStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("WorkCapture") != null;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _client.Dispose();
    }
}

/// <summary>
/// GitHub release API response
/// </summary>
public class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset>? Assets { get; set; }
}

/// <summary>
/// GitHub release asset
/// </summary>
public class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = "";
}

/// <summary>
/// Update check result
/// </summary>
public class ReleaseInfo
{
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string AssetName { get; set; } = "";
}
