---
name: yellowfox-browser
description: "Use when an agent needs to work through YellowFox browser profiles: list imported Dolphin/YellowFox profiles, start or stop a profile, get the Playwright/CDP endpoint, attach automation, open sites, or diagnose profile/proxy/cookie startup issues through the YellowFox CLI."
---

# YellowFox Browser

## Preconditions

- YellowFox Desktop must be running; the CLI talks to its named pipe `yellowfox-agent`.
- Run commands from the repository root `D:\YandexDisk\Coding\Arbitrazh\YellowFox`.
- Use `dotnet run --no-build --project YellowFox.Cli -- ... --json` unless the CLI executable has been published.

## Core Commands

Start YellowFox Desktop if it is not running:

```powershell
dotnet run --no-build --project YellowFox.Cli -- desktop start --json
dotnet run --no-build --project YellowFox.Cli -- desktop status --json
```

Regular CLI commands auto-start YellowFox Desktop when the agent pipe is unavailable and a built `YellowFox.Desktop.exe` can be found.

List profiles:

```powershell
dotnet run --no-build --project YellowFox.Cli -- profile list --json
```

Start a profile by id or exact name:

```powershell
dotnet run --no-build --project YellowFox.Cli -- profile start --id "NRD GGL3" --json
```

Get endpoint:

```powershell
dotnet run --no-build --project YellowFox.Cli -- profile endpoint --id "NRD GGL3" --json
```

Attach with Playwright:

```powershell
dotnet run --no-build --project YellowFox.Cli -- profile attach --id "NRD GGL3" --json
```

Open a site in a profile, starting the profile first if needed:

```powershell
dotnet run --no-build --project YellowFox.Cli -- profile open --id "NRD GGL3" --url "https://example.com" --json
```

Inspect open pages in the running profile:

```powershell
dotnet run --no-build --project YellowFox.Cli -- profile pages --id "NRD GGL3" --text true --json
```

Click a button/link by visible text in the latest page:

```powershell
dotnet run --no-build --project YellowFox.Cli -- profile click --id "NRD GGL3" --text "Scan My Browser" --json
```

Stop a profile:

```powershell
dotnet run --no-build --project YellowFox.Cli -- profile stop --id "NRD GGL3" --json
```

Create/update/delete profiles:

```powershell
dotnet run --no-build --project YellowFox.Cli -- profile create --name "Test" --proxy-id "YWB-MD3" --width 1920 --height 1080 --json
dotnet run --no-build --project YellowFox.Cli -- profile update --id "Test" --notes "..." --proxy-id none --json
dotnet run --no-build --project YellowFox.Cli -- profile delete --id "Test" --json
```

Import/export cookies:

```powershell
dotnet run --no-build --project YellowFox.Cli -- profile import-cookies --id "NRD GGL3" --file "cookies.json" --json
dotnet run --no-build --project YellowFox.Cli -- profile import-cookies --id "NRD GGL3" --domain "facebook.com" --text "c_user=...; xs=..." --json
dotnet run --no-build --project YellowFox.Cli -- profile export-cookies --id "NRD GGL3" --file "cookies.json" --json
```

Import Dolphin data:

```powershell
dotnet run --no-build --project YellowFox.Cli -- dolphin import --json
```

## Browser Automation

- Parse the JSON response. Success is `ok: true`; errors are under `error.code` and `error.message`.
- `profile start` and `profile endpoint` return `data.endpoint`; attach Playwright Firefox to that WebSocket endpoint.
- Prefer `profile attach` for agent automation. It returns `data.endpoint` and `data.storageStatePath`; connect with Playwright and create your own context using that storage state.
- `profile open` returns `data.endpoint`, `data.url`, and `data.title`; use it when the task only needs to open a site without custom browser automation.
- `profile pages --text true` returns page URL/title/body text from YellowFox Desktop's live browser context; use it for smoke checks such as Pixelscan or Facebook session detection.
- `profile click --text "..."` clicks the first matching visible button/link in the active page and is useful for simple test flows.
- If `endpoint` is null, the profile is not running or startup failed; inspect the profile log from `YellowFox.Desktop/bin/Debug/net8.0/data/profiles/<profile-id>/logs/`.
- Imported Dolphin cookies are stored as `imported-cookies.json` in the profile directory and loaded automatically on YellowFox profile start.
- Imported Dolphin localStorage is stored as `imported-local-storage.json` and registered as a Playwright init script, so matching origins are restored when pages open.
- Authenticated SOCKS5 proxies are supported through YellowFox's local bridge; do not bypass the CLI by launching Camoufox manually for those profiles.

## Proxy Commands

```powershell
dotnet run --no-build --project YellowFox.Cli -- proxy list --json
dotnet run --no-build --project YellowFox.Cli -- proxy test --id "YWB-MD3" --json
dotnet run --no-build --project YellowFox.Cli -- proxy change-ip --id "YWB-MD3" --json
dotnet run --no-build --project YellowFox.Cli -- proxy update --id "YWB-MD3" --enabled false --json
dotnet run --no-build --project YellowFox.Cli -- proxy update --id "YWB-MD3" --ip-change-url "https://provider.example/rotate" --json
dotnet run --no-build --project YellowFox.Cli -- profile update --id "NRD GGL3" --proxy-id "YWB-MD3" --json
```

## Extension Commands

```powershell
dotnet run --no-build --project YellowFox.Cli -- extension list --json
dotnet run --no-build --project YellowFox.Cli -- extension import-url --url "https://addons.mozilla.org/en-US/firefox/addon/darkreader/" --json
dotnet run --no-build --project YellowFox.Cli -- extension import-archive --path "D:\path\addon.xpi" --name "Addon" --json
dotnet run --no-build --project YellowFox.Cli -- extension add --name "Custom" --path "D:\path\unpacked-extension" --json
dotnet run --no-build --project YellowFox.Cli -- extension toggle --id "Dark Reader" --enabled false --json
dotnet run --no-build --project YellowFox.Cli -- extension delete --id "Dark Reader" --json
```

## Bookmark Commands

```powershell
dotnet run --no-build --project YellowFox.Cli -- bookmark list --json
dotnet run --no-build --project YellowFox.Cli -- bookmark add-folder --title "Work" --json
dotnet run --no-build --project YellowFox.Cli -- bookmark add --title "Example" --url "https://example.com" --parent-id "<folder-id>" --json
dotnet run --no-build --project YellowFox.Cli -- bookmark update --id "<bookmark-id>" --title "New title" --json
dotnet run --no-build --project YellowFox.Cli -- bookmark delete --id "<bookmark-id>" --json
```

## Failure Rules

- `desktop_unavailable`: start YellowFox Desktop first.
- `start_failed`: check Camoufox browser installation with `python/venv/Scripts/camoufox.exe version` and the profile log.
- Do not print proxy passwords, Dolphin tokens, cookies, or local session tokens in chat.
