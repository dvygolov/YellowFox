# YellowFox Setup Guide

## Quick Start (Windows)

### 1. Install Prerequisites

**Python:**
```powershell
# Download from https://www.python.org/downloads/
# Or use winget:
winget install Python.Python.3.12
```

**.NET SDK:**
```powershell
# Download from https://dotnet.microsoft.com/download
# Or use winget:
winget install Microsoft.DotNet.SDK.8
```

### 2. Install Python Dependencies

```powershell
cd python
pip install -r requirements.txt
```

This will install:
- `camoufox` - The anti-detect browser
- `playwright` - Browser automation framework

### 3. Verify Installation

```powershell
# Check Python
python --version  # Should be 3.8+

# Check .NET
dotnet --version  # Should be 8.0+

# Check CamouFox
pip show camoufox
```

### 4. Build the Application

```powershell
# From project root
dotnet build
```

### 5. Run YellowFox

```powershell
cd YellowFox.Desktop
dotnet run
```

Or double-click the executable:
```
YellowFox.Desktop\bin\Debug\net8.0\YellowFox.Desktop.exe
```

## Quick Start (Linux/macOS)

### 1. Install Prerequisites

**Python:**
```bash
# Ubuntu/Debian
sudo apt install python3 python3-pip

# macOS
brew install python@3.12
```

**.NET SDK:**
```bash
# Ubuntu/Debian
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 8.0

# macOS
brew install dotnet-sdk
```

### 2. Install Python Dependencies

```bash
cd python
pip3 install -r requirements.txt
```

### 3. Build and Run

```bash
dotnet build
cd YellowFox.Desktop
dotnet run
```

## First Time Usage

### Creating Your First Profile

1. Click **"+ New Profile"**
2. Enter:
   - **Name**: "Test Profile"
   - **Notes**: "Testing YellowFox"
   - **OS**: (defaults to your current OS)
   - **Screen**: Select "1920x1080 (Full HD)"
3. Click outside the dialog or implement Save dialog

### Starting a Browser

1. Find "Test Profile" in the list
2. Click **▶️ Start**
3. Wait a few seconds for CamouFox to launch
4. A browser window will appear with the configured fingerprint
5. Status will change to 🟢

### Verifying Fingerprint

Visit these sites to verify anti-detect is working:
- https://abrahamjuliot.github.io/creepjs/
- https://bot.sannysoft.com/
- https://pixelscan.net/
- https://fingerprint.com/demo/

## Troubleshooting

### "python not found"

**Windows:**
- Add Python to PATH during installation
- Or use full path: `C:\Users\YourName\AppData\Local\Programs\Python\Python312\python.exe`

**Linux/macOS:**
- Use `python3` instead of `python`
- Update BrowserService.cs line 54: change `"python"` to `"python3"`

### "CamouFox not starting"

1. **Check Python script:**
   ```bash
   cd python
   python camoufox-server.py
   # Should show error about missing config (expected)
   ```

2. **Test CamouFox manually:**
   ```bash
   python
   >>> from camoufox.sync_api import Camoufox
   >>> with Camoufox() as browser:
   ...     print(browser.cdp_url)
   ```

3. **Check logs:**
   - Look at Debug console in Visual Studio
   - Or run with `dotnet run` to see output

### "Access denied" or "Database locked"

- Close all YellowFox instances
- Delete `data/yellowfox.db` to start fresh
- Check file permissions on `data/` folder

### Browser starts but .NET doesn't connect

- Check firewall settings
- Ensure Python script prints CDP URL to stdout
- Verify Playwright package is installed: `dotnet list package`

## Development Setup

### Running with Hot Reload

```bash
cd YellowFox.Desktop
dotnet watch run
```

Changes to C# files will auto-rebuild and restart.

### Testing Python Script Manually

```bash
cd python

# Create test config
echo '{"os":"windows","screen":{"maxWidth":1920,"maxHeight":1080},"user_data_dir":"./test_profile"}' > test.json

# Run server
python camoufox-server.py test.json

# Should print CDP URL like: http://localhost:9222
# Browser will stay open until you Ctrl+C
```

### Database Schema

View current profiles:
```bash
sqlite3 data/yellowfox.db "SELECT * FROM profiles;"
```

Reset database:
```bash
rm data/yellowfox.db
# Will be recreated on next run
```

## Next Steps

After successful setup:

1. **Create multiple profiles** with different OS/screen configs
2. **Test concurrent browsers** - start 2-3 profiles simultaneously
3. **Verify fingerprint uniqueness** - each profile should have different fingerprints
4. **Check profile isolation** - each profile stores data separately

## Advanced Configuration

### Custom CamouFox Location

If you want to use a specific CamouFox build:

1. Download portable CamouFox
2. Extract to `camoufox/` folder
3. Update Python script if using custom path

### Performance Tuning

- **Limit concurrent profiles**: Each browser uses ~500MB-1GB RAM
- **Clean profile data**: Old browser cache in `data/profiles/{id}/`
- **SSD recommended**: Profile switching is faster on SSD

## Support

- **CamouFox Issues**: https://github.com/daijro/camoufox/issues
- **YellowFox Issues**: Create issue in this repository
