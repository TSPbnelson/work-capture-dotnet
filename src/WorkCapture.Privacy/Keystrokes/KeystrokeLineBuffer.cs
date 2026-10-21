using System.Text;

namespace WorkCapture.Privacy.Keystrokes;

/// <summary>A completed, UNREDACTED line. Never leaves the privacy assembly.</summary>
internal readonly record struct RawLine(
    string Text, Terminator Terminator, DateTime Start, DateTime End, int KeyCount, bool Uncertain,
    IReadOnlyList<string> Shortcuts);

/// <summary>
/// Rebuilds what was typed from key presses, one "line" at a time. A line ends on Enter, Tab,
/// Escape, a window/context change, idle, or when it reaches <see cref="MaxLength"/>.
///
/// Reconstruction is approximate on purpose: once the cursor moves (arrows, Home, Delete, a
/// mouse click inside the field) the text may be out of order, and the line is flagged
/// <see cref="RawLine.Uncertain"/>. It is never more than a narrative aid.
///
/// Shortcuts (Ctrl/Alt/Win chords) are recorded by key NAME in a separate list and never enter
/// the text, so the clipboard content of a Ctrl+V is never seen at all.
/// </summary>
internal sealed class KeystrokeLineBuffer
{
    public const int MaxLength = 256;

    private readonly StringBuilder _sb = new();
    private readonly List<string> _shortcuts = new();
    private DateTime? _start;
    private DateTime _end;
    private int _keys;
    private bool _uncertain;

    public bool IsEmpty => _keys == 0;

    public RawLine? Add(KeystrokeEvent e)
    {
        _start ??= e.Timestamp;
        _end = e.Timestamp;
        _keys++;

        // AltGr arrives as Ctrl+Alt but produces a real character (@, €, \ on many layouts):
        // a printable character is always text, whatever modifiers the hook reports.
        if (e.Kind == KeyKind.Character && e.Char != '\0' && !char.IsControl(e.Char))
        {
            _sb.Append(e.Char);
            return _sb.Length >= MaxLength ? Flush(Terminator.Overflow) : null;
        }

        if (e.IsChord)
        {
            if (e.Kind == KeyKind.Backspace && (e.Modifiers & KeyModifiers.Ctrl) != 0)
                DeleteWord();
            else
                _shortcuts.Add(ShortcutName(e));
            return null;
        }

        switch (e.Kind)
        {
            case KeyKind.Backspace:
                if (_sb.Length > 0) _sb.Length--;
                return null;
            case KeyKind.Delete:
            case KeyKind.Navigation:
                _uncertain = true;
                return null;
            case KeyKind.Enter: return Flush(Terminator.Enter);
            case KeyKind.Tab: return Flush(Terminator.Tab);
            case KeyKind.Escape: return Flush(Terminator.Escape);
            default: return null;
        }
    }

    /// <summary>Close the current line. Returns null when nothing was typed since the last flush.</summary>
    public RawLine? Flush(Terminator t)
    {
        if (_keys == 0) return null;
        var line = new RawLine(_sb.ToString(), t, _start ?? _end, _end, _keys, _uncertain, _shortcuts.ToArray());
        Reset();
        return line;
    }

    public void Reset()
    {
        // Overwrite before clearing so the builder's buffer doesn't keep the characters.
        for (int i = 0; i < _sb.Length; i++) _sb[i] = '\0';
        _sb.Clear();
        _shortcuts.Clear();
        _start = null;
        _keys = 0;
        _uncertain = false;
    }

    private void DeleteWord()
    {
        int i = _sb.Length;
        while (i > 0 && char.IsWhiteSpace(_sb[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(_sb[i - 1])) i--;
        _sb.Length = i;
    }

    private static string ShortcutName(KeystrokeEvent e)
    {
        var parts = new List<string>(4);
        if ((e.Modifiers & KeyModifiers.Ctrl) != 0) parts.Add("Ctrl");
        if ((e.Modifiers & KeyModifiers.Alt) != 0) parts.Add("Alt");
        if ((e.Modifiers & KeyModifiers.Win) != 0) parts.Add("Win");
        if ((e.Modifiers & KeyModifiers.Shift) != 0) parts.Add("Shift");
        // Key name only: letters/digits/named keys, capped. Never a character from the text.
        var name = new string((e.KeyName ?? "").Where(char.IsLetterOrDigit).Take(12).ToArray());
        parts.Add(name.Length > 0 ? name.ToUpperInvariant() : "?");
        return string.Join("+", parts);
    }
}
