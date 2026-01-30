# Work Capture

A Windows system tray application for automated work tracking and time capture. Designed for MSPs to accurately track billable time with intelligent client detection and screenshot documentation.

## Features

- **Smart Screenshot Capture** - Captures screenshots with change detection to avoid duplicates
- **Client Detection** - Automatically identifies which client you're working on based on:
  - IP address ranges (CIDR notation)
  - Hostname patterns
  - Window title patterns
  - URL patterns
- **Privacy Protection** - Excludes sensitive content (banking sites, password managers, etc.)
- **Activity-Based Capture** - Captures more frequently during active typing/clicking
- **Adaptive Rate** - Reduces capture frequency during idle periods
- **Local Storage** - SQLite database for offline operation
- **MSP Portal Sync** - Syncs work sessions to the MSP Portal API for billing

## Requirements

- Windows 10/11
- .NET 8.0 Runtime
- ~50MB disk space for application
- ~100MB/day for screenshots (with cleanup)

## Installation

1. Download the latest release
2. Run `WorkCapture.exe`
3. Configure API key in settings (optional, for sync)
4. Application will appear in system tray

## Configuration

Configuration files are located in `%LOCALAPPDATA%\WorkCapture\config\`:

### settings.json
```json
{
  "capture": {
    "captureIntervalSeconds": 5,
    "screenshotFormat": "webp",
    "screenshotQuality": 85,
    "changeDetectionThreshold": 10,
    "retentionDays": 30
  },
  "sync": {
    "apiUrl": "https://your-portal.com/api",
    "apiKey": "your-api-key",
    "syncIntervalSeconds": 60
  }
}
```

### clients.json
```json
{
  "clients": [
    {
      "name": "Client Name",
      "code": "CLIENT",
      "rules": [
        { "type": "ip_range", "value": "192.168.1.0/24" },
        { "type": "hostname", "value": ".*pattern.*" },
        { "type": "window_title", "value": ".*AppName.*" }
      ]
    }
  ]
}
```

### privacy.json
```json
{
  "excluded_processes": ["1Password", "KeePass"],
  "excluded_window_titles": [".*password.*", ".*bank.*"],
  "excluded_urls": [".*bank.*", ".*paypal.*"]
}
```

## System Tray Menu

- **Pause/Resume** - Temporarily stop/start capturing
- **Force Capture** - Take an immediate screenshot
- **Sync Now** - Force sync to MSP Portal
- **Show Stats** - Display capture statistics
- **Quit** - Exit the application

## Data Storage

- **Database**: `%LOCALAPPDATA%\WorkCapture\workcapture.db`
- **Screenshots**: `%LOCALAPPDATA%\WorkCapture\screenshots\`

Screenshots are automatically cleaned up based on the retention period setting.

## Building from Source

```bash
# Clone the repository
git clone https://github.com/TSPBnelson/work-capture-dotnet.git
cd work-capture-dotnet

# Build
dotnet build -c Release

# Publish (self-contained)
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

## Architecture

```
WorkCapture/
├── App/
│   └── TrayApplication.cs    # Main app, system tray
├── Capture/
│   ├── ScreenCapture.cs      # Screenshot functionality
│   ├── WindowInfo.cs         # Win32 window metadata
│   └── ChangeDetector.cs     # Perceptual hash comparison
├── Config/
│   ├── Settings.cs           # App settings
│   ├── ClientRules.cs        # Client detection config
│   └── PrivacyRules.cs       # Privacy exclusions
├── Data/
│   ├── Database.cs           # SQLite operations
│   └── Models.cs             # Data models
├── Detection/
│   ├── ActivityMonitor.cs    # Keyboard/mouse hooks
│   ├── ClientDetector.cs     # Client matching engine
│   └── PrivacyFilter.cs      # Privacy filter
└── Sync/
    └── ApiSyncService.cs     # Portal API sync
```

## MSP Portal Integration

Work sessions are synced to the MSP Portal where you can:
- Review captured work sessions
- Approve time entries for billing
- Generate invoices
- View work history by client

## License

MIT License - See LICENSE file for details.

## Contributing

1. Fork the repository
2. Create a feature branch
3. Submit a pull request

## Support

For issues and feature requests, please use the GitHub issue tracker.
