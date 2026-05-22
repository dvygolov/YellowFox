# YellowFox - CamouFox Profile Manager

YellowFox is a desktop application for managing multiple CamouFox browser profiles with anti-detect capabilities.

## Features (MVP Phase 1)

- ✅ **Profile Management**: Create, edit, delete, and clone browser profiles
- ✅ **Fingerprint Configuration**: Configure OS and screen resolution (auto-generates other fingerprint data via BrowserForge)
- ✅ **Browser Control**: Start/Stop CamouFox browsers with isolated profiles
- ✅ **Profile Isolation**: Each profile has its own user data directory
- ✅ **Proxy Management**: HTTP and SOCKS5 proxy configuration per profile
- ✅ **Agent CLI**: JSON CLI that talks to a running YellowFox Desktop instance

## Architecture

- **Frontend**: Avalonia UI (cross-platform .NET desktop)
- **Backend**: Python microservice for CamouFox control
- **Database**: SQLite for profile storage
- **Browser**: CamouFox/Clover 142 target build (portable installation)

## Prerequisites

1. **.NET 8 SDK** - [Download](https://dotnet.microsoft.com/download/dotnet/8.0)
2. **Python 3.8+** - [Download](https://www.python.org/downloads/)
3. **CamouFox** - Portable installation (see Setup)

## Setup

### 1. Install Python Dependencies

```bash
cd python
pip uninstall -y camoufox
pip install -r requirements.txt
```

### 2. Install CamouFox

Install the YellowFox target Camoufox browser build. This pins the active browser
to the fresh CoryKing/Clover 142 line and uses a resumable download because the
stock `camoufox fetch` can stall on large GitHub assets:

```bash
python install-camoufox-browser.py
camoufox version
```

### 3. Build .NET Application

```bash
dotnet build
```

### 4. Run the Application

```bash
cd YellowFox.Desktop
dotnet run
```

Or run the compiled executable:
```bash
YellowFox.Desktop\bin\Debug\net8.0\YellowFox.Desktop.exe
```

### Agent CLI

The CLI requires YellowFox Desktop to be running:

```bash
dotnet run --project YellowFox.Cli -- profile list --json
dotnet run --project YellowFox.Cli -- proxy list --json
```

## Usage

### Creating a Profile

1. Click **"+ New Profile"** button
2. Enter profile name and notes
3. Select operating system (defaults to current OS)
4. Choose screen resolution from presets
5. Click **Save**

### Starting a Browser

1. Find the profile in the list
2. Click **▶️ Start** button
3. CamouFox browser will launch with the configured fingerprint
4. Browser status will show 🟢 (running)

### Stopping a Browser

1. Find the running profile (🟢 status)
2. Click **⏹️ Stop** button
3. Browser will close and status will change to ⚫ (stopped)

### Cloning a Profile

1. Click **📋 Clone** on any profile
2. A copy will be created with "(Copy)" suffix
3. Edit the clone as needed

## Project Structure

```
YellowFox/
├── python/
│   ├── camoufox-server.py     # Python server that launches CamouFox
│   └── requirements.txt       # Python dependencies
├── YellowFox.Desktop/         # Avalonia .NET application
│   ├── Models/                # Data models
│   ├── Services/              # Business logic
│   │   ├── DatabaseService.cs # SQLite database
│   │   └── BrowserService.cs  # Browser process management
│   ├── ViewModels/            # MVVM ViewModels
│   └── Views/                 # UI Views
├── camoufox/                  # CamouFox portable (user installs)
└── data/                      # Runtime data (auto-created)
    ├── yellowfox.db          # SQLite database
    └── profiles/             # Browser profile directories
        ├── {profile-id-1}/
        └── {profile-id-2}/
```

## How It Works

1. **User creates a profile** with minimal config (OS + screen resolution)
2. **.NET app** saves profile to SQLite database
3. **On Start**: .NET generates config JSON and spawns Python script
4. **Python script** launches CamouFox with BrowserForge auto-generating fingerprints
5. **Python prints CDP URL** to stdout
6. **.NET connects** to browser via Playwright CDP
7. **On Stop**: .NET kills Python process, closes browser

## Screen Resolution Presets

- **1920x1080** (Full HD) - Most common desktop
- **1366x768** (Laptop) - Common laptop resolution
- **2560x1440** (2K) - High-res desktop
- **3840x2160** (4K) - Ultra HD
- **1536x864** (HD+) - Alternative laptop size

## Troubleshooting

### Python not found
- Ensure Python is in PATH
- Try `python3` instead of `python` on Linux/macOS

### CamouFox not starting
- Check that CamouFox is installed: `pip list | grep camoufox`
- Verify `python/camoufox-server.py` exists
- Check application logs for error messages

### Database locked
- Close all instances of YellowFox
- Delete `data/yellowfox.db` to reset (WARNING: deletes all profiles)

## Development

### Running in Development

```bash
# Terminal 1: Run .NET app with hot reload
cd YellowFox.Desktop
dotnet watch run

# Terminal 2: Monitor Python script (optional)
cd python
python camoufox-server.py test-config.json
```

### Building for Release

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

## License

This project is for educational purposes. CamouFox is subject to its own license.

## Credits

- **CamouFox**: https://github.com/daijro/camoufox
- **BrowserForge**: https://github.com/daijro/browserforge
- **Avalonia UI**: https://avaloniaui.net/
