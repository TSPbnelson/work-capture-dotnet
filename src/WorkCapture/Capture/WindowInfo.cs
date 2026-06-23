using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using WorkCapture.Data;

namespace WorkCapture.Capture;

/// <summary>
/// Extracts information about the active window using Win32 APIs
/// </summary>
public class WindowInfoExtractor
{
    private IntPtr _lastHwnd;
    private WindowInfo? _lastInfo;

    // Browser process names
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "firefox", "msedge", "brave", "opera", "iexplore"
    };

    // Terminal process names
    private static readonly HashSet<string> Terminals = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "WindowsTerminal", "putty", "kitty",
        "ConEmu64", "mintty", "SecureCRT", "wt"
    };

    // Remote desktop apps
    private static readonly HashSet<string> RemoteDesktop = new(StringComparer.OrdinalIgnoreCase)
    {
        "mstsc", "vmconnect", "VpxClient", "vmware", "VirtualBoxVM", "mRemoteNG"
    };

    // Hostname patterns
    private static readonly Regex[] HostnamePatterns = new[]
    {
        new Regex(@"(?:@|\\\\)([a-zA-Z0-9-]+)", RegexOptions.Compiled),
        new Regex(@"(?:SSH|ssh).*?([a-zA-Z0-9-]+\.[a-zA-Z0-9.-]+)", RegexOptions.Compiled),
        new Regex(@"\[([a-zA-Z0-9-]+)\]", RegexOptions.Compiled),
        new Regex(@"^([a-zA-Z0-9-]+)\s*[-:]", RegexOptions.Compiled),
    };

    // IP pattern
    private static readonly Regex IpPattern = new(@"(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})", RegexOptions.Compiled);

    #region Win32 Imports

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    #endregion

    /// <summary>
    /// Get information about the currently active window
    /// </summary>
    public WindowInfo GetActiveWindowInfo()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
                return new WindowInfo();

            // Get window title
            var titleBuilder = new StringBuilder(512);
            GetWindowText(hwnd, titleBuilder, 512);
            var title = titleBuilder.ToString();

            // Get process info
            GetWindowThreadProcessId(hwnd, out uint processId);
            string processName = "";

            try
            {
                using var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch
            {
                // Process may have exited
            }

            var info = new WindowInfo
            {
                Title = title,
                ProcessName = processName,
                ProcessId = (int)processId
            };

            // Classify window type
            var procLower = processName.ToLowerInvariant();
            info.IsBrowser = Browsers.Contains(procLower);
            info.IsTerminal = Terminals.Contains(procLower);
            info.IsRemoteDesktop = RemoteDesktop.Contains(procLower);

            // Extract hostname from terminal/RDP titles
            if (info.IsTerminal || info.IsRemoteDesktop)
            {
                info.Hostname = ExtractHostname(title);
            }

            // Also check title for IP addresses
            if (string.IsNullOrEmpty(info.Hostname))
            {
                info.Hostname = ExtractIpAddress(title);
            }

            _lastHwnd = hwnd;
            _lastInfo = info;
            return info;
        }
        catch (Exception ex)
        {
            Logger.Error($"Error getting window info: {ex.Message}");
            return new WindowInfo();
        }
    }

    /// <summary>
    /// Enrich a WindowInfo with deterministic text signals from UI Automation:
    /// the browser URL (for browser windows) and a bounded sample of foreground UI text.
    /// Called ONLY for frames that are actually being saved (UIA is cross-process / slow),
    /// and never for sensitive/metadata-only captures. Best-effort: failures leave fields null.
    /// </summary>
    public void EnrichForeground(WindowInfo info)
    {
        try
        {
            var hwnd = _lastHwnd != IntPtr.Zero ? _lastHwnd : GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            if (info.IsBrowser && string.IsNullOrEmpty(info.Url))
                info.Url = UiaTextExtractor.GetBrowserUrl(hwnd);

            info.UiText = UiaTextExtractor.GetForegroundText(hwnd);

            // A hostname/IP may surface in the UI text even when the title didn't have one.
            if (string.IsNullOrEmpty(info.Hostname) && !string.IsNullOrEmpty(info.UiText))
                info.Hostname = ExtractHostname(info.UiText) ?? ExtractIpAddress(info.UiText);
        }
        catch (Exception ex)
        {
            Logger.Debug($"EnrichForeground failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if the active window has changed since last check
    /// </summary>
    public bool HasWindowChanged()
    {
        var currentHwnd = GetForegroundWindow();
        return currentHwnd != _lastHwnd;
    }

    /// <summary>
    /// Check if the window title has changed
    /// </summary>
    public bool HasTitleChanged(string currentTitle)
    {
        return _lastInfo?.Title != currentTitle;
    }

    private static string? ExtractHostname(string title)
    {
        foreach (var pattern in HostnamePatterns)
        {
            var match = pattern.Match(title);
            if (match.Success)
            {
                var hostname = match.Groups[1].Value;
                if (IsValidHostname(hostname))
                {
                    return hostname.ToLowerInvariant();
                }
            }
        }
        return null;
    }

    private static string? ExtractIpAddress(string title)
    {
        var match = IpPattern.Match(title);
        if (match.Success)
        {
            var ip = match.Groups[1].Value;
            var parts = ip.Split('.');
            if (parts.All(p => int.TryParse(p, out var v) && v >= 0 && v <= 255))
            {
                return ip;
            }
        }
        return null;
    }

    private static bool IsValidHostname(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 253)
            return false;

        // Exclude common non-hostname strings
        var exclude = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "administrator", "admin", "user", "root", "home", "desktop", "select", "new"
        };

        return !exclude.Contains(name) &&
               Regex.IsMatch(name, @"^[a-zA-Z0-9][a-zA-Z0-9-]*[a-zA-Z0-9]$|^[a-zA-Z0-9]$");
    }
}
