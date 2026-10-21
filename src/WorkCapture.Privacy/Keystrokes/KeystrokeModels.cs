namespace WorkCapture.Privacy.Keystrokes;

public enum KeyKind
{
    Character,   // a printable character, already translated by the keyboard layout
    Enter,
    Tab,
    Backspace,
    Delete,
    Escape,
    Navigation,  // arrows, Home/End, PgUp/PgDn: the cursor moved, so reconstruction is approximate
    Other        // F-keys, media keys, lone modifiers
}

[Flags]
public enum KeyModifiers
{
    None = 0,
    Ctrl = 1,
    Alt = 2,
    Win = 4,
    Shift = 8,
}

/// <summary>
/// One key press, as delivered by the platform hook adapter. <see cref="KeyName"/> is used only
/// for shortcut tokens like [Ctrl+S]; the character itself travels in <see cref="Char"/>.
/// </summary>
public readonly record struct KeystrokeEvent(
    KeyKind Kind,
    char Char,
    KeyModifiers Modifiers,
    string KeyName,
    DateTime Timestamp)
{
    public static KeystrokeEvent Of(char c, DateTime ts) => new(KeyKind.Character, c, KeyModifiers.None, c.ToString(), ts);
    public static KeystrokeEvent Key(KeyKind k, DateTime ts, KeyModifiers m = KeyModifiers.None, string name = "")
        => new(k, '\0', m, name.Length > 0 ? name : k.ToString(), ts);
    public static KeystrokeEvent Shortcut(KeyModifiers m, string name, DateTime ts) => new(KeyKind.Other, '\0', m, name, ts);

    /// <summary>Ctrl / Alt / Win held: this is a command, not text (Shift alone is just a capital).</summary>
    public bool IsChord => (Modifiers & (KeyModifiers.Ctrl | KeyModifiers.Alt | KeyModifiers.Win)) != 0;
}

/// <summary>
/// What the keys were typed into, sampled by the adapter at key time. Every field the adapter
/// could not determine must be left null/false-safe: <see cref="FocusIsPassword"/> = null means
/// "unknown" and is treated as a password field (fail closed).
/// </summary>
public sealed record InputContext(
    string ProcessName,
    string WindowTitle,
    string? Url = null,
    bool? FocusIsPassword = false,
    bool IsSecureDesktop = false,
    bool IsRemoteSession = false,
    bool IsTerminal = false,
    long WindowHandle = 0)
{
    /// <summary>
    /// Identity of the input target: a change flushes the buffer and resets credential state.
    /// The window HANDLE, not the title, when the adapter supplies one: titles change while you
    /// type (Notepad adds "*", a terminal retitles after ssh), and a title change must not reset
    /// "mask the line after ssh" at exactly the moment the password is typed.
    /// </summary>
    public string WindowKey => WindowHandle != 0 ? $"hwnd:{WindowHandle}" : $"{ProcessName}\u001f{WindowTitle}";
}

public enum Terminator { Enter, Tab, Escape, WindowChange, ContextChange, Idle, Overflow, Shutdown }

/// <summary>
/// The only thing that leaves the sanitizer. <see cref="Text"/> has passed every redaction layer;
/// <see cref="Redactions"/> lists the kinds that fired (never the values).
/// </summary>
public sealed record SanitizedEntry(
    DateTime Start,
    DateTime End,
    string ProcessName,
    string WindowTitle,
    string Text,
    Terminator Terminator,
    IReadOnlyList<string> Redactions,
    bool Suppressed,
    string? SuppressReason,
    int KeyCount,
    IReadOnlyList<string> Shortcuts);
