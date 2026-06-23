using System.Text;
using System.Windows.Automation;
using WorkCapture.Data;

namespace WorkCapture.Capture;

/// <summary>
/// Extracts deterministic TEXT signals from the foreground window via UI Automation:
///   - the active browser's URL (the omnibox / address bar)
///   - a bounded sample of visible UI text (surfaces RMM client-name strings, hostnames,
///     etc. even when the screenshot itself is black, e.g. full-screen RDP).
///
/// Everything here is best-effort and heavily guarded: any failure returns null and never
/// disrupts the capture loop. UI Automation is cross-process and can be slow, so callers
/// invoke this ONLY for frames that are actually being saved (not on skipped/deduped ticks).
/// </summary>
public static class UiaTextExtractor
{
    // Chromium/Firefox omnibox accessibility names (locale-dependent; we try several then
    // fall back to the first URL-shaped Edit value).
    private static readonly string[] OmniboxNames =
    {
        "Address and search bar",
        "Address field",
        "Search or enter address",
        "Search with Google or enter address",
    };

    /// <summary>Read the active browser's URL from its address bar. Query string stripped.</summary>
    public static string? GetBrowserUrl(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return null;

            // 1) Targeted: a known omnibox name (fast, precise).
            foreach (var name in OmniboxNames)
            {
                var cond = new AndCondition(
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
                    new PropertyCondition(AutomationElement.NameProperty, name));
                var el = SafeFindFirst(root, cond);
                var val = ReadValue(el);
                if (!string.IsNullOrWhiteSpace(val)) return NormalizeUrl(val!);
            }

            // 2) Fallback: first Edit whose value looks like a URL (has a dot, no spaces).
            var anyEdit = SafeFindFirst(root, new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Edit));
            var v2 = ReadValue(anyEdit);
            if (!string.IsNullOrWhiteSpace(v2) && v2!.Contains('.') && !v2.Contains(' '))
                return NormalizeUrl(v2);
        }
        catch (Exception ex)
        {
            Logger.Debug($"UIA URL extract failed: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// Bounded sample of the foreground window's visible text (content view). Hard caps on
    /// nodes visited, depth, and total length keep it cheap and safe on huge browser trees.
    /// </summary>
    public static string? GetForegroundText(IntPtr hwnd, int maxChars = 2000)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            if (root == null) return null;

            var sb = new StringBuilder();
            var walker = TreeWalker.ContentViewWalker;
            var stack = new Stack<(AutomationElement el, int depth)>();
            stack.Push((root, 0));
            int visited = 0;
            const int maxNodes = 400, maxDepth = 6;

            while (stack.Count > 0 && sb.Length < maxChars && visited < maxNodes)
            {
                var (el, depth) = stack.Pop();
                visited++;
                try
                {
                    var name = el.Current.Name;
                    if (!string.IsNullOrWhiteSpace(name) && name.Length <= 200)
                        sb.Append(name).Append(' ');
                }
                catch { /* element went away */ }

                if (depth < maxDepth)
                {
                    try
                    {
                        var child = walker.GetFirstChild(el);
                        while (child != null && stack.Count < maxNodes)
                        {
                            stack.Push((child, depth + 1));
                            child = walker.GetNextSibling(child);
                        }
                    }
                    catch { /* subtree unavailable */ }
                }
            }

            var text = sb.ToString().Trim();
            if (text.Length == 0) return null;
            return text.Length > maxChars ? text.Substring(0, maxChars) : text;
        }
        catch (Exception ex)
        {
            Logger.Debug($"UIA text extract failed: {ex.Message}");
            return null;
        }
    }

    private static AutomationElement? SafeFindFirst(AutomationElement root, Condition cond)
    {
        try { return root.FindFirst(TreeScope.Descendants, cond); }
        catch { return null; }
    }

    private static string? ReadValue(AutomationElement? el)
    {
        if (el == null) return null;
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var p))
                return ((ValuePattern)p).Current.Value;
        }
        catch { }
        return null;
    }

    /// <summary>Trim and drop the query string for privacy (keep scheme+host+path).</summary>
    private static string NormalizeUrl(string raw)
    {
        var url = raw.Trim();
        var q = url.IndexOf('?');
        if (q >= 0) url = url.Substring(0, q);
        return url.Length > 300 ? url.Substring(0, 300) : url;
    }
}
