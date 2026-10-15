<#
.SYNOPSIS
  Builds a patched FlaUI.WebDriver that can click/type without moving the
  physical mouse or keyboard (so UI tests do not disturb your work).

.DESCRIPTION
  Clones upstream FlaUI.WebDriver at a pinned commit, applies
  no-mouse-input.patch (adds the appium:clickStrategy / appium:typeStrategy
  capabilities), and publishes a self-contained single-file exe.

  Usage:
    powershell -File build-fork.ps1                 # build only
    powershell -File build-fork.ps1 -Run            # build, then start on :4723
    powershell -File build-fork.ps1 -WorkDir D:\flaui-src -OutDir D:\flaui-bin

  Capabilities added by the patch (session creation):
    appium:clickStrategy  "invoke" (default) | "mouse"
        invoke = click via UI Automation patterns, else Win32 PostMessage;
        never moves the physical cursor. "mouse" restores upstream SendInput.
    appium:typeStrategy   "value" (default) | "keys"
        value = set text via UI Automation ValuePattern; never touches the keyboard.
        "keys" restores upstream physical typing.

  Example session capabilities:
    { "platformName": "windows", "appium:automationName": "FlaUI",
      "appium:app": "C:\\path\\app.exe",
      "appium:clickStrategy": "invoke", "appium:typeStrategy": "value" }
#>
param(
    [string]$WorkDir,
    [string]$OutDir,
    [string]$BaseCommit = '3cd26e6',
    [string]$RepoUrl = 'https://github.com/FlaUI/FlaUI.WebDriver.git',
    [string]$Dotnet = 'dotnet',
    [int]$Port = 4723,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$patch = Join-Path $scriptDir 'no-mouse-input.patch'
if (-not $WorkDir) { $WorkDir = Join-Path $scriptDir '.work\FlaUI.WebDriver' }
if (-not $OutDir) { $OutDir = Join-Path $scriptDir 'publish' }

if (-not (Test-Path $patch)) { throw "Patch not found: $patch" }

# --- source ---
if (-not (Test-Path (Join-Path $WorkDir '.git'))) {
    Write-Host "Cloning $RepoUrl -> $WorkDir"
    git clone $RepoUrl $WorkDir
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed' }
}
git -C $WorkDir fetch --all --tags | Out-Null
git -C $WorkDir checkout --force $BaseCommit
if ($LASTEXITCODE -ne 0) { throw "git checkout $BaseCommit failed" }
git -C $WorkDir clean -fd | Out-Null

Write-Host "Applying $patch"
git -C $WorkDir apply --whitespace=nowarn $patch
if ($LASTEXITCODE -ne 0) { throw 'git apply failed (already applied?)' }

# --- build ---
Write-Host "Publishing to $OutDir"
& $Dotnet publish (Join-Path $WorkDir 'src\FlaUI.WebDriver\FlaUI.WebDriver.csproj') `
    -c Release -r win-x64 -o $OutDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$exe = Join-Path $OutDir 'FlaUI.WebDriver.exe'
Write-Host "Built: $exe"

# --- run ---
if ($Run) {
    Write-Host "Starting on http://localhost:$Port/ (close the window or stop the process to end)"
    Start-Process -FilePath $exe -ArgumentList "--urls=http://localhost:$Port/"
}
