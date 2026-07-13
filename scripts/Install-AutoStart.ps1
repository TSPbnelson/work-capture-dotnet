<#
.SYNOPSIS
    Installs a self-healing auto-start for WorkCapture on a capture VM.

.DESCRIPTION
    WorkCapture must run in the interactive desktop session (it screen-captures via
    GDI, which a session-0 Windows service cannot do). So instead of a service, this
    registers two Scheduled Tasks (via schtasks.exe, for maximum reliability):

      * "WorkCapture AutoStart" - at logon: start WorkCapture immediately.
      * "WorkCapture Watchdog"  - every 5 minutes: if WorkCapture.exe is NOT running,
        start it. (If it's already running, does nothing.)

    Both run in the interactive user session, only while the user is logged on
    (a disconnected RDP session still counts as logged on, so the watchdog keeps
    working while you're disconnected too).

    This is the fix for the failure where BRAD-QTP sat dead for 8 days after a
    reboot/crash with nothing restarting it. Now: a crash self-heals within 5 min,
    and a reboot relaunches at logon.

    Run this ONCE per capture VM, in an ELEVATED PowerShell:
        powershell -ExecutionPolicy Bypass -File .\Install-AutoStart.ps1

    Idempotent - re-running refreshes the tasks and the watchdog script.
#>

$ErrorActionPreference = 'Stop'

$Exe          = 'C:\WorkCapture\WorkCapture.exe'
$WatchdogPs1  = 'C:\WorkCapture\watchdog.ps1'
$WatchdogVbs  = 'C:\WorkCapture\watchdog-hidden.vbs'
$AutoStartTN  = 'WorkCapture AutoStart'
$WatchdogTN   = 'WorkCapture Watchdog'

if (-not (Test-Path $Exe)) {
    Write-Warning "WorkCapture.exe not found at $Exe. Install WorkCapture first, then re-run."
    exit 1
}

# 1. Write the watchdog script that both tasks call (check-and-start, no duplicates).
@"
# WorkCapture watchdog: start the agent only if it isn't already running.
if (-not (Get-Process WorkCapture -ErrorAction SilentlyContinue)) {
    Start-Process '$Exe'
}
"@ | Set-Content -Path $WatchdogPs1 -Encoding ASCII
Write-Host "Wrote watchdog: $WatchdogPs1" -ForegroundColor Gray

# 1b. Write a VBScript launcher that runs the watchdog with NO visible window.
#     Launching powershell.exe directly from the task flashes a console window every time it
#     fires (every 5 min) - even with -WindowStyle Hidden, because that flag only hides the
#     host window AFTER the process starts, so conhost blinks first. wscript's Run(cmd, 0, False)
#     starts it hidden from the first frame (0 = hidden window, False = fire-and-forget), so
#     there is no flash at all. This is the fix for the recurring PowerShell popup.
@"
' Launch the WorkCapture watchdog with no visible window.
CreateObject("WScript.Shell").Run "powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""$WatchdogPs1""", 0, False
"@ | Set-Content -Path $WatchdogVbs -Encoding ASCII
Write-Host "Wrote hidden launcher: $WatchdogVbs" -ForegroundColor Gray

# 2. Register the two scheduled tasks. Path has no spaces, so no inner quoting needed.
#    Omitting /RU makes the task run as the current user, only when logged on
#    (interactive session) - required for screen capture.
#    Launch via wscript + the .vbs so the task never flashes a console window.
$tr = "wscript.exe $WatchdogVbs"

schtasks.exe /Create /TN $AutoStartTN /TR $tr /SC ONLOGON /RL LIMITED /F | Out-Null
Write-Host "Registered '$AutoStartTN' (start at logon)." -ForegroundColor Green

schtasks.exe /Create /TN $WatchdogTN /TR $tr /SC MINUTE /MO 5 /RL LIMITED /F | Out-Null
Write-Host "Registered '$WatchdogTN' (relaunch within 5 min if it dies)." -ForegroundColor Green

# 3. Start it now so capture begins immediately.
if (-not (Get-Process WorkCapture -ErrorAction SilentlyContinue)) {
    Start-Process $Exe
    Write-Host "Started WorkCapture now." -ForegroundColor Green
} else {
    Write-Host "WorkCapture already running." -ForegroundColor Gray
}

Write-Host ""
Write-Host "Done. WorkCapture will now auto-start at logon and self-heal if it crashes." -ForegroundColor Cyan
