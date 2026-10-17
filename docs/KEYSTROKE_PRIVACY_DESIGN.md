# WorkCapture: accuracy review and keystroke privacy design

**Date:** 2026-10-17 · **Branch:** `feature/keystroke-redaction` (local only, not pushed) ·
**PM:** `workcapture-reliability`, phase O

This document goes with the activity-stream plan already drafted today
(`~/.claude/plans/sequential-popping-beacon.md`, Phases 0–5). That plan fixes **time**. This one
adds what it left open: the **"redaction layers"** that typed text is meant to pass through, plus
the client-ID gaps found in the agent source.

---

## 1. Gaps found in the agent (verified in source, 2026-10-17)

| # | Gap | Where | Effect |
|---|---|---|---|
| 1 | **OCR and UI text are uploaded unredacted.** Anything readable on screen, such as a secret in a config file or terminal, goes to the server and the vision prompt as-is. | `App/TrayApplication.cs` `WriteSignalSidecar` | Privacy exposure today, before any keystroke work |
| 2 | **The 6 `ip_range` rules never run.** `Detect()` is never called with `ipAddress`, and an IP from a title is stored in `Hostname`. | `Detection/ClientDetector.cs` (all 3 call sites in `TrayApplication.cs`); `config/clients.json` (JNJ, TIER3, SNYDER ×4) | Small. The server matcher matches IPs from ITDocs (`backend/api/signal_matcher.py:42,178`), and the agent's code is only a hint |
| 3 | **The remote-app list misses most remote tools.** It has mstsc, vmconnect, VMware, VirtualBox and mRemoteNG, but not ScreenConnect, Splashtop, AnyDesk, TeamViewer or RDCMan, so hostnames are never pulled from their titles. | `Capture/WindowInfo.cs:31-34` | Lost client evidence for every non-RDP remote session |
| 4 | **An ITDocs result always beats local rules, whatever its confidence, and it's a blocking HTTP call on every tick.** | `Detection/ClientDetector.cs:51` | Slow ticks and a weak ITDocs title match outranks a strong URL rule (already Phase 1 of the plan) |
| 5 | **Time is measured from screenshots.** The idle gate drops frames after 90 s and the server caps gaps at 30 s. | `TrayApplication.cs` idle gate; `segmenter.py` | ~25% loss measured Oct 5–9 (from today's plan, not re-measured here) |
| 6 | **No record of session state** (lock, disconnect, unlock). | none | Can't tell "away" from "reading" |
| 7 | **Hook handlers run on the hook thread.** Fine today because they only count, but Windows silently removes a low-level hook whose callback takes over 1 s. | `Detection/ActivityMonitor.cs` | A keystroke path that does work in the callback would quietly stop counting |
| 8 | **The work summary is thin** (moondream plus titles). | `Vision/*` | Phase 4 of the plan; typed text plus shortcut names feed it |

## 2. What I'm borrowing from other tools

| Technique | Source | Use in WorkCapture |
|---|---|---|
| Heartbeat merge with `pulsetime` (an event extends while app and title stay the same and the next reading arrives within `max(poll×1.5, poll+1)`) | ActivityWatch `aw_transform/heartbeats.py` (MPL-2.0) | Phase 2 segmenter: 7 s polling gives a 10.5 s pulsetime, so one missed frame never splits a segment |
| **Away time is backdated to the last input**, not to when the idle timeout fired; a screen lock counts as away immediately | ActivityWatch `aw-watcher-afk` | Phase 2: idle starts at `GetLastInputInfo`, and lock/disconnect from `WTSRegisterSessionNotification` ends a segment at once |
| "You were away 12 min — which client?" prompt on return | RescueTime | Optional Phase 3 alternative: a tray prompt on return from more than 5 min idle. Brad's answer is ground truth |
| One reassignment in the UI becomes a rule for the future | Timing (Mac) | Work Review: "make this a signal" on manual reassignment feeds `client_signals`, which has only 58 rows today |
| Recall **drops the whole snapshot** on a sensitive match | Microsoft Recall | **Rejected.** Dropping loses billable time. We redact the text and keep the minutes |
| Vendor token prefixes, private-key blocks, generic "keyword near value" rule | gitleaks (MIT), GitHub token formats | Ported as regexes in `SecretRedactor` (rewritten, not copied) |
| Entropy limits: base64 ≥ 4.5 bits/char, hex ≥ 3.0 | Yelp detect-secrets | `TokenShape.IsHighEntropySecret` |
| Card numbers confirmed with the Luhn check | Microsoft Presidio (MIT) | `SecretRedactor.IsLuhnCard` |
| Live-key verification | trufflehog | **Not used.** It's AGPL-3.0, and verifying a key sends it over the network |

## 3. Keystroke privacy architecture

All of this lives in `src/WorkCapture.Privacy`, a plain `net8.0` library with no Windows
dependency, so it's testable on claudellama. The Windows hook adapter is the only new code that
would go into the app.

```
 SharpHook EventLoopGlobalHook ──► ConcurrentQueue (callback: enqueue only, never > 1 ms)
                                          │
                       single drain thread│  + InputContext sampled from a UIA focus cache
                                          ▼
 ┌──────────────────────────── KeystrokeSanitizer ─────────────────────────────┐
 │ Layer 1  ContextGate      before buffering → counts only for:               │
 │          password field (UIA IsPassword / EM_GETPASSWORDCHAR), focus         │
 │          unknown, secure desktop, password managers, credential/UAC/logon    │
 │          processes, sign-in/MFA/vault titles & URLs, RDP/ScreenConnect       │
 │ Layer 2  Credential context (whole line masked):                             │
 │          A. next line after ssh/sudo/runas/mysql -p/Enter-PSSession/...      │
 │          B. one-word line sent with Enter right after a Tab field            │
 │          C. standalone one-word line, ≥ 6 chars, ≥ 2 char classes            │
 │ Layer 3  SecretRedactor (Keystrokes mode) on what's left: vendor tokens,     │
 │          JWT, PEM keys, URL creds, password=..., CLI flags, FortiGate/Cisco, │
 │          net user, ConvertTo-SecureString, cards (Luhn), SSN, entropy,       │
 │          password-shaped tokens (8–64 chars, ≥ 3 char classes)               │
 │ Fail-closed: any exception → buffer wiped, "[REDACTED:filter_error]"         │
 └──────────────────────────────────────────────────────────────────────────────┘
                                          │  SanitizedEntry only
                                          ▼
                     SQLite typed_text → upload → server re-checks with the same rules
```

**Properties the tests enforce:**
- **Raw characters never reach disk.** Only `SanitizedEntry` leaves the library, and the line
  buffer overwrites its characters before clearing.
- **Every key is counted on every path**, including suppressed and filter-error paths, so
  privacy never costs billable minutes.
- **Paste is never read.** Ctrl+V is recorded as the shortcut name `Ctrl+V` and the clipboard is
  never touched.
- **AltGr characters count as text**, not shortcuts, so a password containing `@` typed on a
  German layout doesn't leak into the shortcut list.
- **Masks have a fixed length** (`[REDACTED:kind]`), so a mask doesn't reveal the secret's length.
- **Redaction is idempotent.** Running it twice gives the same result.
- **Regexes have 100–200 ms timeouts.** A timeout throws, which fails closed.
- **ITDocs hostnames can be passed as `safeTerms`** so `STR-DC01` survives as client evidence
  while an unknown `ABC-XY99` gets masked.

**Two modes:**
- `General` mode, for OCR, UI text and titles, uses the pattern and entropy rules only. Screen
  text is full of model numbers and hostnames, and it's the main client signal.
- `Keystrokes` mode adds the password-shape rule, because a typed password has no label next to it.

## 3b. Windows adapter (`src/WorkCapture/Detection/KeystrokeRecorder.cs`, built 2026-10-17)

- **Off by default.** It's turned on per VM with `"keystrokes": { "enabled": true }` in
  `C:\WorkCapture\config\settings.json`. Remote-session text has no setting at all and is
  always counts only.
- **Reuses ActivityMonitor's existing SharpHook hook** (`ActivityMonitor.Hook`) rather than
  installing a second one. SharpHook 5.3.1 has no `EventLoopGlobalHook`, so the callbacks only
  enqueue. The queue is capped at 10,000; keys past the cap are dropped, never stored, and
  counted as `dropped`.
- **One drain thread** (MTA) holds each key for 150 ms so focus can settle, then works out the
  context:
  - Window identity is the foreground HWND.
  - The process name is cached per PID.
  - A password box is detected by `EM_GETPASSWORDCHAR` (50 ms `SendMessageTimeout`) or UIA
    `FocusedElement.IsPassword`. If UIA throws, the result is null, which means counts only.
  - The secure desktop is detected by an `OpenInputDesktop` name other than "Default".
  - Remote sessions are identified by process (mstsc, msrdc, ScreenConnect.WindowsClient,
    strwinclt, AnyDesk, TeamViewer, RDCMan and others) or by title marker.
  - The context is re-probed after Tab, Enter, Escape or a mouse click, when the window changes,
    and at least every 5 s.
- **Key mapping:** characters come from `KeyTyped` and control keys from `KeyPressed`.
  Ctrl, Alt or Win chords are stored as key names only. AltGr (Ctrl+Alt) characters are
  treated as text.
- **Storage:** SQLite table `typed_text` holds sanitized rows only. Window titles go through
  `General` redaction before they're written. Rows age out after `retentionDays`. Upload
  isn't built yet; it waits for the server endpoint.
- **Lifecycle:** it follows Start, Stop, Pause and Resume. Pause flushes open lines and drops
  later keys. `[Status]` gains
  `typed: keys= suppressed= entries= filter_errors= dropped= probe_fail=`.
- **Library fix found while wiring:** the sanitizer now identifies a window by its HWND, not its
  title. Titles change mid-typing (Notepad adds `*`, a terminal retitles after `ssh`), and a
  title change used to reset the "mask the line after ssh" rule at exactly the wrong moment.
  Two regression tests cover it.
- **Verified here:** the whole solution builds in Release with `EnableWindowsTargeting=true`
  (0 errors; the 12 warnings are all ImageSharp 3.1.7 advisories that were already there), and
  117/117 tests pass. **Not verified:** any runtime behaviour on Windows. That needs a VM.

## 4. Tests

There are 117 tests in `tests/WorkCapture.Privacy.Tests`, all passing:
`docker run … mcr.microsoft.com/dotnet/sdk:8.0 dotnet test tests/WorkCapture.Privacy.Tests`.

- **Redactor:** every vendor token type, PEM, JWT, URL creds, labelled values, 15 CLI forms, cards
  (valid, invalid and repeated-digit), SSN, entropy, the hex vs GUID split, the false-positive
  set (PowerShell, paths, GUIDs, UPNs, versions, `DOMAIN\user`), mask length, idempotence, and a
  50k-char input that must finish quickly.
- **Sanitizer:** reconstruction (Backspace, Ctrl+Backspace, overflow), shortcuts, AltGr, every
  gate reason, focus moving into a password box mid-window, window-change flushing, each
  credential-context rule, idle flush, fail-closed with a throwing redactor, and a full morning
  end to end where no typed password appears in any entry.

## 5. Residual risks (stated plainly)

1. **A lowercase passphrase can slip through.** One typed as part of a sentence in an ordinary
   app (`the pw is correcthorse`), with no prompting command before it, no Tab-then-Enter, and
   only 1–2 character classes, gets past every layer. Lowering the shape threshold to 2 classes
   would catch it, at the cost of masking a lot of ordinary words with digits.
2. **Whether the low-level hook sees keys in full-screen RDP is unverified.** That matters when
   "Apply Windows key combinations" is set to the remote computer. It affects counts, not leaks,
   since remote text is suppressed by default. Brad has to test it on a VM.
3. **Raw characters sit in process memory until garbage collection.** They're never written, but
   a memory dump of WorkCapture would contain them.
4. **Input to elevated (admin) windows may not reach a non-elevated hook** (Windows UIPI). This is
   unverified, and like item 2 it lowers counts rather than leaking.

## 6. Decisions Brad needs to make

1. **Text inside RDP/ScreenConnect sessions.** I recommend **counts only** (the default). The
   VM can't see whether the remote field is a password box, and every client login happens
   there. Screen OCR already captures what was typed once it's echoed on screen.
2. **Typed text going to the Claude API in Phase 4.** It's redacted, but it's still client
   content leaving the building, which is the same question already open for frames in Phase 4.

## 7. Remaining build (PM phase O)

| Step | Who | Est. |
|---|---|---|
| Redact the sidecar's OCR, UI text and title (gap 1). Small change, closes today's exposure | me | 1 h |
| ~~Windows adapter~~ **built 2026-10-17** (section 3b). Session lock/disconnect state was deferred to Phase 1's activity buckets | me | done |
| Server endpoint, table, agent upload of `typed_text`, and a Python port checked against shared test vectors (Rule 18) | me | 5 h |
| Client-ID fixes (gaps 2–4) | me | 2 h |
| Remote-text decision; install on the four VMs; full-screen RDP hook test | Brad | ~1 h |
