using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using SharpHook;
using SharpHook.Native;
using WorkCapture.Data;
using WorkCapture.Privacy.Keystrokes;
using WorkCapture.Privacy.Redaction;

namespace WorkCapture.Detection;

/// <summary>
/// Windows adapter between the global keyboard hook and <see cref="KeystrokeSanitizer"/>.
/// OFF unless settings.json has <c>"keystrokes": { "enabled": true }</c>.
///
/// Threading, and why it matters:
///   - Hook callbacks (hook thread) ONLY enqueue a small struct. Windows silently unhooks a
///     low-level hook whose callback takes more than ~1 s (LowLevelHooksTimeout), and it would
///     take the activity counts down with it, so nothing slow ever runs there.
///   - One drain thread does everything else: focus/password probing (UI Automation is
///     cross-process and can block), sanitizing, and the SQLite write.
///   - The drain thread holds each key until it is <see cref="SettleDelay"/> old, so that after a
///     Tab or click the target app has moved focus before we ask "is this a password box?".
///
/// Privacy: raw characters exist only in the queue and inside the sanitizer. Only
/// <see cref="SanitizedEntry"/> rows reach the database. Text typed into RDP / ScreenConnect /
/// other remote-control windows is never kept (counts only); that is the ContextGate default and
/// is deliberately not exposed as a setting.
/// </summary>
public sealed class KeystrokeRecorder : IDisposable
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan ProbeMaxAge = TimeSpan.FromSeconds(5);
    private const int MaxQueue = 10_000;

    private enum RawKind : byte { Pressed, Typed, MouseDown }

    private readonly record struct RawKey(RawKind Kind, KeyCode Code, char Char, ModifierMask Mask, DateTime At);

    private readonly Database _db;
    private readonly KeystrokeSanitizer _sanitizer;
    private readonly SecretRedactor _titleRedactor = new();
    private readonly ConcurrentQueue<RawKey> _queue = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly FocusProbe _probe = new();

    private IGlobalHook? _hook;
    private Thread? _drainThread;
    private volatile bool _running;
    private volatile bool _paused;
    private int _queued;
    private bool _idleFlushed = true;
    private DateTime _lastKeyAt = DateTime.MinValue;

    // Diagnostics for the [Status] line. Written by the drain thread, read anywhere.
    public long EntriesWritten;
    public long DroppedOverflow;
    public long ProbeFailures;
    public long TotalKeys => _sanitizer.TotalKeys;
    public long SuppressedKeys => _sanitizer.SuppressedKeys;
    public long FilterErrors => _sanitizer.FilterErrors;

    public KeystrokeRecorder(Database db, IEnumerable<string>? safeTerms = null)
    {
        _db = db;
        _sanitizer = new KeystrokeSanitizer(safeTerms: safeTerms);
    }

    public void Attach(IGlobalHook hook)
    {
        _hook = hook;
        hook.KeyPressed += OnKeyPressed;
        hook.KeyTyped += OnKeyTyped;
        hook.MousePressed += OnMousePressed;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _drainThread = new Thread(DrainLoop) { IsBackground = true, Name = "KeystrokeDrain" };
        _drainThread.SetApartmentState(ApartmentState.MTA);   // UI Automation client: not on an STA UI thread
        _drainThread.Start();
        Logger.Info("Keystroke recorder started (remote-session text: counts only)");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _signal.Set();
        _drainThread?.Join(TimeSpan.FromSeconds(5));
        Logger.Info($"Keystroke recorder stopped | {StatusText()}");
    }

    /// <summary>Paused = keys are counted by ActivityMonitor as before, but nothing is buffered.</summary>
    public void SetPaused(bool paused)
    {
        _paused = paused;
        _signal.Set();
    }

    public string StatusText() =>
        $"keys={TotalKeys} suppressed={SuppressedKeys} entries={Interlocked.Read(ref EntriesWritten)} " +
        $"filter_errors={FilterErrors} dropped={Interlocked.Read(ref DroppedOverflow)} probe_fail={Interlocked.Read(ref ProbeFailures)}";

    // ---- hook thread: enqueue only ------------------------------------------------------------

    private void Enqueue(RawKey k)
    {
        if (_paused || !_running) return;
        if (Interlocked.Increment(ref _queued) > MaxQueue)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref DroppedOverflow);   // dropped raw keys are never stored: fail safe
            return;
        }
        _queue.Enqueue(k);
        _signal.Set();
    }

    private void OnKeyPressed(object? sender, KeyboardHookEventArgs e) =>
        Enqueue(new RawKey(RawKind.Pressed, e.Data.KeyCode, '\0', e.RawEvent.Mask, DateTime.Now));

    private void OnKeyTyped(object? sender, KeyboardHookEventArgs e) =>
        Enqueue(new RawKey(RawKind.Typed, e.Data.KeyCode, e.Data.KeyChar, e.RawEvent.Mask, DateTime.Now));

    private void OnMousePressed(object? sender, MouseHookEventArgs e) =>
        Enqueue(new RawKey(RawKind.MouseDown, KeyCode.VcUndefined, '\0', e.RawEvent.Mask, DateTime.Now));

    // ---- drain thread -------------------------------------------------------------------------

    private void DrainLoop()
    {
        while (_running)
        {
            try
            {
                _signal.WaitOne(TimeSpan.FromMilliseconds(250));

                while (_queue.TryPeek(out var head))
                {
                    var wait = head.At + SettleDelay - DateTime.Now;
                    if (wait > TimeSpan.Zero && _running) { Thread.Sleep(wait); }
                    if (!_queue.TryDequeue(out var k)) break;
                    Interlocked.Decrement(ref _queued);
                    Process(k);
                }

                if (_paused)
                    Write(_sanitizer.Flush(Terminator.Shutdown));
                else if (!_idleFlushed && DateTime.Now - _lastKeyAt >= KeystrokeSanitizer.IdleFlushAfter)
                {
                    Write(_sanitizer.Flush(Terminator.Idle));
                    _idleFlushed = true;
                }
            }
            catch (Exception ex)
            {
                // Never let the drain thread die; the sanitizer already fails closed per key.
                Logger.Warning($"Keystroke drain error: {ex.GetType().Name}");
                Thread.Sleep(1000);
            }
        }

        // Shutdown: drop anything still queued unprocessed (never stored), close open lines.
        while (_queue.TryDequeue(out _)) Interlocked.Decrement(ref _queued);
        try { Write(_sanitizer.Flush(Terminator.Shutdown)); } catch { /* fail closed: nothing written */ }
    }

    private void Process(RawKey k)
    {
        if (k.Kind == RawKind.MouseDown)
        {
            _probe.Invalidate();   // a click can move focus into a password box
            return;
        }

        var ev = ToEvent(k);
        if (ev is null) return;

        if (ev.Value.Kind is KeyKind.Tab or KeyKind.Enter or KeyKind.Escape)
            _probe.Invalidate();   // after these keys, focus is about to move

        InputContext ctx;
        try
        {
            ctx = _probe.Current();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref ProbeFailures);
            ctx = new InputContext("", "", FocusIsPassword: null);   // unknown → counts only
        }

        _lastKeyAt = k.At;
        _idleFlushed = false;
        Write(_sanitizer.OnKey(ev.Value, ctx));

        // Focus moves AFTER Tab/Enter is delivered: re-probe on the next key, not this one.
        if (ev.Value.Kind is KeyKind.Tab or KeyKind.Enter or KeyKind.Escape)
            _probe.Invalidate();
    }

    /// <summary>
    /// SharpHook delivers KeyPressed for every key, then KeyTyped with the layout-translated
    /// character for printable keys. Text comes from KeyTyped; control keys and shortcuts from
    /// KeyPressed; each physical key is turned into exactly one event.
    /// </summary>
    private static KeystrokeEvent? ToEvent(RawKey k)
    {
        var mods = KeyModifiers.None;
        if ((k.Mask & ModifierMask.Ctrl) != 0) mods |= KeyModifiers.Ctrl;
        if ((k.Mask & ModifierMask.Alt) != 0) mods |= KeyModifiers.Alt;
        if ((k.Mask & ModifierMask.Meta) != 0) mods |= KeyModifiers.Win;
        if ((k.Mask & ModifierMask.Shift) != 0) mods |= KeyModifiers.Shift;

        // AltGr is reported as Ctrl+Alt and produces real characters (@ € \ on many layouts).
        bool altGr = (mods & (KeyModifiers.Ctrl | KeyModifiers.Alt)) == (KeyModifiers.Ctrl | KeyModifiers.Alt);

        if (k.Kind == RawKind.Typed)
        {
            if (k.Char == '\0' || char.IsControl(k.Char)) return null;          // Enter/Tab/Ctrl+C come via KeyPressed
            if ((mods & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Win)) != 0 && !altGr) return null;  // chord, handled on press
            return new KeystrokeEvent(KeyKind.Character, k.Char, mods, "", k.At);
        }

        switch (k.Code)
        {
            case KeyCode.VcEnter or KeyCode.VcNumPadEnter: return KeystrokeEvent.Key(KeyKind.Enter, k.At, mods);
            case KeyCode.VcTab: return KeystrokeEvent.Key(KeyKind.Tab, k.At, mods);
            case KeyCode.VcBackspace: return KeystrokeEvent.Key(KeyKind.Backspace, k.At, mods);
            case KeyCode.VcDelete: return KeystrokeEvent.Key(KeyKind.Delete, k.At, mods);
            case KeyCode.VcEscape: return KeystrokeEvent.Key(KeyKind.Escape, k.At, mods);
            case KeyCode.VcLeft or KeyCode.VcRight or KeyCode.VcUp or KeyCode.VcDown
                or KeyCode.VcHome or KeyCode.VcEnd or KeyCode.VcPageUp or KeyCode.VcPageDown:
                return KeystrokeEvent.Key(KeyKind.Navigation, k.At, mods);
        }

        // Shortcut: Ctrl / Win / Alt (alone, not AltGr) + a key. Recorded by key NAME only.
        bool chord = !altGr && (mods & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Win)) != 0;
        if (chord && !IsModifierKey(k.Code))
            return KeystrokeEvent.Shortcut(mods, k.Code.ToString().Replace("Vc", ""), k.At);

        return null;   // printable keys arrive as KeyTyped; lone modifiers/F-keys carry no text
    }

    private static bool IsModifierKey(KeyCode c) => c is KeyCode.VcLeftControl or KeyCode.VcRightControl
        or KeyCode.VcLeftAlt or KeyCode.VcRightAlt or KeyCode.VcLeftShift or KeyCode.VcRightShift
        or KeyCode.VcLeftMeta or KeyCode.VcRightMeta;

    private void Write(IReadOnlyList<SanitizedEntry> entries)
    {
        foreach (var e in entries)
        {
            if (e.KeyCount == 0 && !e.Redactions.Contains("filter_error")) continue;
            // Titles are not covered by the sanitizer; screen-text rules apply to them.
            var title = _titleRedactor.Redact(e.WindowTitle, RedactionMode.General).Text;
            _db.InsertTypedText(e with { WindowTitle = title });
            Interlocked.Increment(ref EntriesWritten);
        }
    }

    public void Dispose()
    {
        Stop();
        if (_hook != null)
        {
            _hook.KeyPressed -= OnKeyPressed;
            _hook.KeyTyped -= OnKeyTyped;
            _hook.MousePressed -= OnMousePressed;
        }
        _signal.Dispose();
    }

    // =========================================================================================

    /// <summary>
    /// Answers "what is the user typing into?" for the drain thread. Cached, and re-probed when
    /// invalidated (Tab/Enter/Escape/click), when the foreground window changes, or every 5 s.
    /// Any probe it cannot complete reports FocusIsPassword = null, which the gate treats as a
    /// password field.
    /// </summary>
    private sealed class FocusProbe
    {
        private static readonly HashSet<string> RemoteProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "mstsc", "msrdc", "vmconnect", "VpxClient", "vmware", "vmrc", "VirtualBoxVM", "mRemoteNG",
            "RDCMan", "royalts", "RemoteDesktopManager", "RemoteDesktopManager64",
            "ScreenConnect.WindowsClient", "ScreenConnect.ClientService", "strwinclt", "SRFeature",
            "AnyDesk", "TeamViewer", "TeamViewer_Desktop", "Splashtop", "rustdesk", "LogMeIn",
        };

        private static readonly string[] RemoteTitleMarkers =
            { "Remote Desktop Connection", " - ScreenConnect", "ScreenConnect", "AnyDesk", "TeamViewer", "Splashtop" };

        private static readonly HashSet<string> TerminalProcesses = new(StringComparer.OrdinalIgnoreCase)
        {
            "cmd", "powershell", "pwsh", "WindowsTerminal", "OpenConsole", "conhost", "putty", "kitty",
            "ConEmu64", "mintty", "SecureCRT", "wt", "MobaXterm",
        };

        private readonly Dictionary<uint, string> _procNames = new();
        private IntPtr _hwnd;
        private InputContext? _cached;
        private DateTime _probedAt;
        private bool _invalid = true;

        public void Invalidate() => _invalid = true;

        public InputContext Current()
        {
            var hwnd = GetForegroundWindow();
            var title = WindowTitle(hwnd);

            if (!_invalid && _cached != null && hwnd == _hwnd && DateTime.Now - _probedAt < ProbeMaxAge)
                return _cached with { WindowTitle = title };   // title may change; identity is the handle

            var threadId = GetWindowThreadProcessId(hwnd, out var pid);
            var process = ProcessName(pid);

            bool remote = RemoteProcesses.Contains(process)
                          || RemoteTitleMarkers.Any(m => title.Contains(m, StringComparison.OrdinalIgnoreCase));

            _cached = new InputContext(
                ProcessName: process,
                WindowTitle: title,
                Url: null,
                FocusIsPassword: remote ? false : FocusIsPassword(threadId),   // remote is counts-only anyway
                IsSecureDesktop: IsSecureDesktop(),
                IsRemoteSession: remote,
                IsTerminal: TerminalProcesses.Contains(process),
                WindowHandle: hwnd.ToInt64());
            _hwnd = hwnd;
            _probedAt = DateTime.Now;
            _invalid = false;
            return _cached;
        }

        /// <summary>
        /// true if either probe says password; false only if UI Automation positively says
        /// "not a password"; null (= treat as password) if UI Automation could not answer.
        /// </summary>
        private static bool? FocusIsPassword(uint threadId)
        {
            // 1) Classic Win32 edit with ES_PASSWORD: EM_GETPASSWORDCHAR returns the mask char.
            var gti = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (threadId != 0 && GetGUIThreadInfo(threadId, ref gti) && gti.hwndFocus != IntPtr.Zero)
            {
                var ok = SendMessageTimeout(gti.hwndFocus, EM_GETPASSWORDCHAR, IntPtr.Zero, IntPtr.Zero,
                                            SMTO_ABORTIFHUNG | SMTO_BLOCK, 50, out var result);
                if (ok != IntPtr.Zero && result != IntPtr.Zero) return true;
            }

            // 2) UI Automation: covers browsers, WPF, UWP, and in-page focus moves that keep the HWND.
            try
            {
                var focused = AutomationElement.FocusedElement;
                return focused?.Current.IsPassword;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>UAC / Ctrl+Alt+Del / lock screen run on the Winlogon desktop, not "Default".</summary>
        private static bool IsSecureDesktop()
        {
            var desk = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
            if (desk == IntPtr.Zero) return true;   // can't open the input desktop: assume secure
            try
            {
                var buf = new byte[256];
                if (!GetUserObjectInformation(desk, UOI_NAME, buf, buf.Length, out _)) return true;
                var name = Encoding.Unicode.GetString(buf).TrimEnd('\0');
                return !name.Equals("Default", StringComparison.OrdinalIgnoreCase);
            }
            finally { CloseDesktop(desk); }
        }

        private string ProcessName(uint pid)
        {
            if (_procNames.TryGetValue(pid, out var n)) return n;
            try { n = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { n = ""; }
            if (_procNames.Count > 256) _procNames.Clear();
            _procNames[pid] = n;
            return n;
        }

        private static string WindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(512);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        // ---- Win32 ----
        private const uint EM_GETPASSWORDCHAR = 0x00D2;
        private const uint SMTO_BLOCK = 0x0001, SMTO_ABORTIFHUNG = 0x0002;
        private const uint DESKTOP_READOBJECTS = 0x0001;
        private const int UOI_NAME = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize, flags;
            public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
            public RECT rcCaret;
        }

        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO gui);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
                                                        uint flags, uint timeoutMs, out IntPtr result);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseDesktop(IntPtr hDesk);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool GetUserObjectInformation(IntPtr hObj, int index, byte[] info, int len, out int needed);
    }
}
