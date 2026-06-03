# YellowFox

YellowFox is a Windows desktop profile manager for Camoufox/Clover browser
profiles. It is built for managing isolated browser profiles with proxies,
bookmarks, extensions, cookie import/export, and an automation-friendly local
CLI.

The project uses Avalonia UI and .NET for the desktop application, SQLite for
local storage, and Python broker scripts to launch and control Camoufox.

> Current platform status: Windows is the supported target. macOS and Linux are
> not supported as full desktop targets yet because browser window management,
> process handling, icons, and taskbar integration currently use Windows-specific
> behavior.

## Features

- Profile management: create, edit, clone, delete, search, and bulk-select
  browser profiles.
- Camoufox launch control: start and stop isolated browser profiles.
- Fingerprint basics: OS and screen profile settings are passed to Camoufox.
- Proxy management: HTTP and SOCKS5 proxies, validation, status, country flag,
  and mobile-proxy IP rotation URL.
- Cookie workflows: import cookies from JSON or `name=value` strings, export
  cookies, and persist imported cookies into the profile.
- Extensions: import unpacked folders, XPI/ZIP archives, or AMO/direct URLs.
- Bookmarks: shared bookmarks and folder tree synchronization.
- Profile logs: per-profile log viewer for browser startup and import issues.
- Agent CLI: JSON command interface for automation and external agents.
- GitHub Actions release build for Windows x64.

## Repository Layout

```text
YellowFox/
├── YellowFox.Desktop/       # Avalonia desktop app
│   ├── Models/              # Profile, proxy, extension, bookmark models
│   ├── Services/            # SQLite, browser, proxy, extension, agent services
│   ├── ViewModels/          # MVVM application logic
│   └── Views/               # Avalonia XAML views
├── YellowFox.Cli/           # Agent CLI for Desktop named-pipe commands
├── YellowFox.Tests/         # xUnit tests
├── python/                  # Camoufox broker and setup scripts
├── .agents/skills/          # Agent workflow notes for this repo
├── SETUP.md                 # Older setup guide
├── ARCHITECTURE.md          # Architecture notes
└── YellowFox.sln            # .NET solution
```

Runtime data is created under the app data directory used by
`DatabaseService`, including:

- `yellowfox.db` - SQLite database.
- `profiles/<profile-id>/` - Camoufox user data directories.
- `extensions/` - imported shared extensions.

Do not commit runtime data, browser caches, cookies, or profile directories.

## Prerequisites

For development on Windows:

- Windows 10 or newer.
- .NET 8 SDK.
- Python 3.11 or 3.12.
- Git.

Optional but useful:

- GitHub CLI (`gh`) for releases and repository management.
- Visual Studio 2022 or JetBrains Rider.

## Setup

Clone the repository:

```powershell
git clone https://github.com/dvygolov/YellowFox.git
cd YellowFox
```

Install Python dependencies:

```powershell
cd python
python -m pip install --upgrade pip
python -m pip install -r requirements.txt
cd ..
```

Install the pinned Camoufox/Clover browser build used by YellowFox:

```powershell
cd python
python install-camoufox-browser.py
cd ..
```

Build the solution:

```powershell
dotnet build YellowFox.sln
```

Run the desktop app:

```powershell
dotnet run --project YellowFox.Desktop
```

Run tests:

```powershell
dotnet test
```

## Desktop Usage

1. Open YellowFox Desktop.
2. Create proxies in the Proxies section if needed.
3. Create a profile in the Profiles section.
4. Choose OS, screen size, proxy, and notes.
5. Start the profile from the profiles table.
6. Use the actions menu or right-click context menu for edit, clone, cookies,
   logs, and deletion.

### Cookies

Cookies can be imported in two formats:

- Browser JSON export format.
- `name=value; name2=value2` text with a target domain.

When a profile is not running, imported cookies are saved and applied on the
next profile start. When a profile is already running, YellowFox applies cookies
to the live browser context through the broker.

### Extensions

Extensions can be imported from:

- A folder containing `manifest.json`.
- A `.xpi` or `.zip` archive.
- An AMO add-on page URL or a direct archive URL.

Imported enabled extensions are synchronized into profile directories during
profile startup.

### Proxies

Supported proxy types:

- HTTP.
- SOCKS5.
- Authenticated SOCKS5 through the local bridge.

Mobile proxy rotation can be configured with an IP change URL. The UI enables
the Change IP action only when a proxy has such a URL.

## Agent CLI

The CLI talks to the running YellowFox Desktop app through the local named pipe
`yellowfox-agent`. If Desktop is not running, supported commands can auto-start
it from the built desktop executable.

Examples:

```powershell
dotnet run --project YellowFox.Cli -- desktop status --json
dotnet run --project YellowFox.Cli -- profile list --json
dotnet run --project YellowFox.Cli -- profile start --id "NRD Lazy 4" --json
dotnet run --project YellowFox.Cli -- profile stop --id "NRD Lazy 4" --json
dotnet run --project YellowFox.Cli -- proxy list --json
dotnet run --project YellowFox.Cli -- extension import-url --url "https://addons.mozilla.org/en-US/firefox/addon/darkreader/" --json
```

Common command groups:

- `desktop status`, `desktop start`.
- `profile list/start/stop/open/pages/click/create/update/delete/clone`.
- `profile import-cookies`, `profile export-cookies`.
- `proxy list/add/update/delete/test/change-ip`.
- `extension list/add/import-url/import-archive/toggle/delete`.
- `bookmark list/add/add-folder/update/delete`.

CLI responses are JSON and follow this shape:

```json
{
  "ok": true,
  "data": {}
}
```

Failures return:

```json
{
  "ok": false,
  "error": {
    "code": "error_code",
    "message": "Human-readable message"
  }
}
```

## Release Builds

Windows release builds are produced by GitHub Actions.

Manual build from a local checkout:

```powershell
dotnet publish YellowFox.Desktop/YellowFox.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/publish/YellowFox.Desktop
dotnet publish YellowFox.Cli/YellowFox.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o artifacts/publish/YellowFox.Cli
```

The GitHub workflow publishes:

- `YellowFox.Desktop` self-contained Windows x64 app.
- `YellowFox.Cli` self-contained Windows x64 CLI.
- Python broker scripts copied into the desktop output by the project file.
- A `.zip` artifact for every run.
- A GitHub Release asset when a tag matching `v*` is pushed.

Create a release tag:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

## Development Notes

Recommended checks before pushing:

```powershell
dotnet test
python -m py_compile python/camoufox-broker.py python/camoufox-native-broker.py
```

The desktop app may lock `YellowFox.Desktop.exe` and
`YellowFox.Desktop.dll` while it is running. If a normal build fails with a
file-lock error, close the running app or use a compile-only check:

```powershell
dotnet build YellowFox.Desktop/YellowFox.Desktop.csproj -p:UseAppHost=false -p:CopyBuildOutputToOutputDirectory=false
```

## Troubleshooting

### Camoufox does not start

Check the profile log from the UI or under the profile data directory. Then
verify Python dependencies and the pinned browser installation:

```powershell
python -m pip show cloverlabs-camoufox
python python/install-camoufox-browser.py
```

### Proxy works in a browser but YellowFox import/open fails

Check the profile log and proxy validator output. Some flows use broker HTTP
calls and some use browser startup. The UI logs proxy validation, broker endpoint
startup, and cookie import errors.

### Build fails because files are locked

Close the running YellowFox Desktop instance or use the compile-only build
command shown in Development Notes.

### Cookie import reports an error

Use the profile log. YellowFox stores parsed cookies before live import. If the
profile is not running, the cookies will be applied at the next startup.

## Platform Status

YellowFox is currently Windows-first.

Avalonia and .NET can run on macOS/Linux, and parts of the codebase are portable,
but full profile launch behavior is not currently validated outside Windows.
Known Windows-specific areas include:

- Window visibility and monitoring.
- Taskbar icon/grouping behavior.
- Process-tree handling.
- Camoufox native window integration.
- Paths used by local setup scripts.

Cross-platform support should be added through a platform abstraction layer
instead of scattering OS checks through services.

## Security Notes

YellowFox can store and apply browser cookies and proxy credentials. Treat the
runtime data directory as sensitive.

Do not publish:

- `data/` directories.
- Browser profile directories.
- Cookie exports.
- Proxy credentials.
- Local `.env` or secret files.

## License

No license file is currently included. Until a license is added, all rights are
reserved by the repository owner.

Third-party projects have their own licenses:

- Avalonia UI: https://avaloniaui.net/
- Camoufox/Clover: refer to the upstream project license.
- Playwright: https://playwright.dev/
- Simple Icons: https://simpleicons.org/
