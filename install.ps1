# WorkCapture Installer
# Downloads latest release, installs to C:\WorkCapture, adds to startup
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Token "ghp_xxxxx"
#
# The GitHub token needs 'repo' scope for private repo access.
# Token is saved to config for future in-app updates.

param(
    [string]$Token = ""
)

$ErrorActionPreference = "Stop"
$InstallDir = "C:\WorkCapture"
$ConfigDir = "$InstallDir\config"
$Repo = "TSPbnelson/work-capture-dotnet"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  WorkCapture Installer" -ForegroundColor Cyan
Write-Host "  Tech Server Pro" -ForegroundColor Cyan
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
    @"
{
  "capture": {
    "capture_interval_seconds": 5,
    "capture_on_window_change": true,
    "capture_on_keyboard_activity": true,
    "keyboard_activity_window_ms": 500,
    "screenshot_format": "webp",
    "screenshot_quality": 85,
    "max_screenshot_width": 1920,
    "retention_days": 30,
    "change_detection_threshold": 5,
    "idle_timeout_seconds": 60
  },
  "sync": {
    "api_url": "https://msp.techserverpro.com/api",
    "api_key": "",
    "sync_interval_seconds": 60,
    "sync_screenshots": false
  },
  "storage": {
    "data_dir": "C:\\WorkCapture",
    "screenshot_dir": "C:\\WorkCapture\\screenshots",
    "database_path": "C:\\WorkCapture\\workcapture.db",
    "log_path": "C:\\WorkCapture\\logs"
  },
  "vision": {
    "enabled": true,
    "service_url": "http://192.168.1.16:8001",
    "timeout_seconds": 45,
    "analyze_every_nth": 1,
    "async_analysis": true
  },
  "update": {
    "github_token": "$Token"
  }
}
"@ | Set-Content "$ConfigDir\settings.json" -Encoding UTF8
    Write-Host "Created default settings.json" -ForegroundColor Green
}

if (-not (Test-Path "$ConfigDir\clients.json")) {
    Write-Host "Creating default clients.json..." -ForegroundColor White
    @"
{
  "clients": [
    {
      "code": "HMC",
      "name": "Hotel McCoy",
      "patterns": {
        "window_titles": ["Hotel McCoy", "HMC", "Cloudbeds"],
        "hostnames": ["HMC-*"],
        "urls": ["*.hotelmccoy.com"]
      }
    },
    {
      "code": "JNJ",
      "name": "JNJ Services",
      "patterns": {
        "window_titles": ["JNJ", "InTime", "J-APPSERVER", "J-SQL"],
        "hostnames": ["J-*", "QTP-*"],
        "urls": ["*.jnjtransportation.com"]
      }
    },
    {
      "code": "NLT",
      "name": "Next Level Tech",
      "patterns": {
        "window_titles": ["Next Level", "NLT", "LearningMatters"],
        "hostnames": ["NLT-*"],
        "urls": ["*.learningmatters-ed.org"]
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
