<#
.SYNOPSIS
    Installs a self-healing auto-start for WorkCapture on a capture VM.

.DESCRIPTION
    WorkCapture must run in the interactive desktop session (it screen-captures via
    GDI, which a session-0 Windows service cannot do). So instead of a service, this
    registers a Scheduled Task that:

      * Starts WorkCapture at logon (covers VM reboot -> next RDP logon).
      * Runs a watchdog every 5 minutes: if WorkCapture.exe is NOT running, start it.
        (If it's already running, the check does nothing.)

    This is the fix for the failure where BRAD-QTP sat dead for 8 days after a
    reboot/crash with nothing restarting it. With this task, a crash self-heals within
    5 minutes and a reboot relaunches on logon.

    Run this ONCE per capture VM, in an ELEVATED PowerShell:
        powershell -ExecutionPolicy Bypass -File .\Install-AutoStart.ps1

    Idempotent - re-running just refreshes the task.
#>

$ErrorActionPreference = 'Stop'

$TaskName = 'WorkCapture AutoStart'
$Exe      = 'C:\WorkCapture\WorkCapture.exe'

if (-not (Test-Path $Exe)) {
    Write-Warning "WorkCapture.exe not found at $Exe. Install WorkCapture first, then re-run."
    exit 1
}

# Watchdog: start WorkCapture only if it isn't already running (avoids duplicate instances).
$checkCmd = "if (-not (Get-Process WorkCapture -ErrorAction SilentlyContinue)) { Start-Process '$Exe' }"
$encoded  = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($checkCmd))

$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -WindowStyle Hidden -EncodedCommand $encoded"

# Trigger 1: at logon of the current user (covers reboot -> logon/RDP).
$atLogon = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"

# Trigger 2: a repeating watchdog every 5 minutes, indefinitely.
$watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date) `
    -RepetitionInterval (New-TimeSpan -Minutes 5) `
    -RepetitionDuration ([TimeSpan]::MaxValue)

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

# Run in the interactive session as the current user (needed for screen capture).
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" `
    -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName `
    -Action $action -Trigger $atLogon, $watchdog `
    -Settings $settings -Principal $principal -Force | Out-Null

Write-Host "Installed scheduled task '$TaskName'." -ForegroundColor Green
Write-Host "  - Starts WorkCapture at logon" -ForegroundColor Gray
Write-Host "  - Watchdog relaunches it within 5 min if it dies" -ForegroundColor Gray

# Kick it once now so it's running immediately.
if (-not (Get-Process WorkCapture -ErrorAction SilentlyContinue)) {
    Start-Process $Exe
    Write-Host "Started WorkCapture now." -ForegroundColor Green
} else {
    Write-Host "WorkCapture already running." -ForegroundColor Gray
}
