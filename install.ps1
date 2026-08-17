# WorkCapture Installer
# Downloads latest release, installs to C:\WorkCapture, adds to startup
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File install.ps1 -ClientCode "ETG" -Token "ghp_xxxxx"
#
# -ClientCode is MANDATORY and is this machine's BILLING ANCHOR. One VM serves exactly one
# tenant: 100% of work on that VM bills to this client and its sub-clients. Get it wrong and
# the machine bills a whole tenant's work to the wrong MSP.
#
# After installing you must ALSO register the machine server-side, or nothing locks it:
#   INSERT INTO capture_machines (machine_name, anchor_code) VALUES ('<HOSTNAME>', '<CODE>');
#
# The GitHub token needs 'repo' scope for private repo access.
# Token is saved to config for future in-app updates.

param(
    [Parameter(Mandatory=$true)]
    [ValidatePattern('^[A-Za-z0-9_-]{2,10}$')]
    [string]$ClientCode,

    [string]$ApiKey = "",
    [string]$Token = ""
)

$ClientCode = $ClientCode.ToUpper()

$ErrorActionPreference = "Stop"
$InstallDir = "C:\WorkCapture"
$ConfigDir = "$InstallDir\config"
$Repo = "TSPbnelson/work-capture-dotnet"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  WorkCapture Installer" -ForegroundColor Cyan
Write-Host "  Tier3 MSP" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Prompt for token if not provided
if (-not $Token) {
    Write-Host "This is a private GitHub repo. A personal access token is required." -ForegroundColor Yellow
    Write-Host "Create one at: https://github.com/settings/tokens" -ForegroundColor Gray
    Write-Host "Required scope: repo (Full control of private repositories)" -ForegroundColor Gray
    Write-Host ""
    $Token = Read-Host "Enter GitHub token"
    if (-not $Token) {
        Write-Host "ERROR: Token is required for private repo access." -ForegroundColor Red
        exit 1
    }
}

$headers = @{
    "User-Agent" = "WorkCapture-Installer"
    "Authorization" = "Bearer $Token"
}

# Check if running
$running = Get-Process -Name "WorkCapture" -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "WorkCapture is running. Stopping it..." -ForegroundColor Yellow
    Stop-Process -Name "WorkCapture" -Force
    Start-Sleep -Seconds 2
}

# Get latest release from GitHub
Write-Host "Checking for latest release..." -ForegroundColor White
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers $headers
$version = $release.tag_name
$asset = $release.assets | Where-Object { $_.name -like "*win-x64*.zip" } | Select-Object -First 1

if (-not $asset) {
    Write-Host "ERROR: No win-x64 ZIP found in release $version" -ForegroundColor Red
    exit 1
}

Write-Host "Latest version: $version" -ForegroundColor Green
Write-Host "Asset: $($asset.name)" -ForegroundColor Gray

# Download
$tempDir = "$env:TEMP\WorkCapture_Install"
if (Test-Path $tempDir) { Remove-Item $tempDir -Recurse -Force }
New-Item -ItemType Directory -Path $tempDir | Out-Null

$zipPath = "$tempDir\$($asset.name)"
Write-Host "Downloading..." -ForegroundColor White
Invoke-WebRequest -Uri $asset.url -Headers @{
    "User-Agent" = "WorkCapture-Installer"
    "Authorization" = "Bearer $Token"
    "Accept" = "application/octet-stream"
} -OutFile $zipPath
Write-Host "Downloaded: $zipPath" -ForegroundColor Green

# Extract
Write-Host "Extracting to $InstallDir..." -ForegroundColor White
if (-not (Test-Path $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir | Out-Null
}
Expand-Archive -Path $zipPath -DestinationPath $InstallDir -Force
Write-Host "Extracted." -ForegroundColor Green

# Create default config if not exists
if (-not (Test-Path $ConfigDir)) {
    New-Item -ItemType Directory -Path $ConfigDir | Out-Null
}

if (-not (Test-Path "$ConfigDir\settings.json")) {
    Write-Host "Creating default settings.json..." -ForegroundColor White

    # ⛔ KEYS MUST BE camelCase.
    # Settings.cs binds with JsonNamingPolicy.CamelCase + PropertyNameCaseInsensitive.
    # Case-insensitivity does NOT bridge underscores, so a snake_case key binds to NOTHING and
    # silently falls back to the code default. This file wrote an entirely snake_case config
    # until 2026-08-17, so every value below was ignored on every install ever performed —
    # including machineDefaultClientCode (the billing anchor), apiKey (sync auth) and
    # gitHubToken (self-update). Only settings that are genuinely per-machine are emitted here;
    # everything else is deliberately omitted so Settings.cs stays the single source of defaults.
    @"
{
  "capture": {
    "machineDefaultClientCode": "$ClientCode"
  },
  "sync": {
    "apiKey": "$ApiKey"
  },
  "update": {
    "gitHubToken": "$Token"
  }
}
"@ | Set-Content "$ConfigDir\settings.json" -Encoding UTF8
    Write-Host "Created settings.json (billing anchor: $ClientCode)" -ForegroundColor Green
} else {
    Write-Host "settings.json already exists - leaving it alone." -ForegroundColor Yellow
    Write-Host "  Verify it uses camelCase keys and has a machineDefaultClientCode." -ForegroundColor Gray
}

if (-not (Test-Path "$ConfigDir\clients.json")) {
    Write-Host "Creating default clients.json..." -ForegroundColor White
    @"
{
  "clients": [
    {
      "code": "JNJ",
      "name": "JNJ Services",
      "patterns": {
        "window_titles": ["JNJ", "InTime", "J-APPSERVER", "J-SQL"],
        "hostnames": ["J-*", "QTP-*"],
        "urls": ["*.jnjtransportation.com"]
      }
    }
  ]
}
"@ | Set-Content "$ConfigDir\clients.json" -Encoding UTF8
    Write-Host "Created default clients.json" -ForegroundColor Green
}

if (-not (Test-Path "$ConfigDir\privacy.json")) {
    Write-Host "Creating default privacy.json..." -ForegroundColor White
    @"
{
  "excluded_processes": [
    "KeePass",
    "1Password",
    "Bitwarden",
    "LastPass"
  ],
  "excluded_title_patterns": [
    "*password*",
    "*credential*",
    "*secret*",
    "*vault*"
  ],
  "sensitive_url_patterns": [
    "*bank*",
    "*paypal*",
    "*venmo*"
  ]
}
"@ | Set-Content "$ConfigDir\privacy.json" -Encoding UTF8
    Write-Host "Created default privacy.json" -ForegroundColor Green
}

# Add to Windows startup
Write-Host "Adding to Windows startup..." -ForegroundColor White
$exePath = "$InstallDir\WorkCapture.exe"
$regPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
Set-ItemProperty -Path $regPath -Name "WorkCapture" -Value "`"$exePath`""
Write-Host "Added to startup." -ForegroundColor Green

# Create data directories
New-Item -ItemType Directory -Path "$InstallDir\screenshots" -Force | Out-Null
New-Item -ItemType Directory -Path "$InstallDir\logs" -Force | Out-Null

# Cleanup
Remove-Item $tempDir -Recurse -Force

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "  Installation complete!" -ForegroundColor Green
Write-Host "  Version: $version" -ForegroundColor Green
Write-Host "  Location: $InstallDir" -ForegroundColor Green
Write-Host "  Startup: Enabled" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""

# Ask to start now
$start = Read-Host "Start WorkCapture now? (Y/n)"
if ($start -ne "n") {
    Write-Host "Starting WorkCapture..." -ForegroundColor White
    Start-Process $exePath
    Write-Host "Running! Look for the green dot in your system tray." -ForegroundColor Green
}
