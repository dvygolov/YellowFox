<#
.SYNOPSIS
  Smoke test for the patched FlaUI.WebDriver: drives the YellowFox Proxies tab
  entirely through REST with appium:clickStrategy=invoke and verifies the
  physical cursor never moves.

  Prereq: build-fork.ps1 has produced publish\FlaUI.WebDriver.exe and it is
  running on -Url.
#>
param(
    [string]$Url = 'http://127.0.0.1:4723',
    [string]$App = 'D:\YandexDisk\Coding\Arbitrazh\YellowFox\YellowFox.Desktop\bin\Debug\net8.0\YellowFox.Desktop.exe',
    [string]$OutDir = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CursorProbe {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    public static string Pos() { POINT p; GetCursorPos(out p); return p.X + "," + p.Y; }
}
'@

$appJson = $App.Replace('\', '\\')
$caps = @"
{"capabilities":{"alwaysMatch":{
  "platformName":"windows",
  "appium:automationName":"FlaUI",
  "appium:app":"$appJson",
  "appium:clickStrategy":"invoke",
  "appium:typeStrategy":"value"
}}}
"@
$s = Invoke-RestMethod -Method Post -Uri "$Url/session" -ContentType 'application/json' -Body $caps -TimeoutSec 60
$sid = $s.value.sessionId
"SID=$sid"

function Find-Els($using, $value) {
    $b = (@{ using = $using; value = $value } | ConvertTo-Json -Compress)
    $r = Invoke-RestMethod -Method Post -Uri "$Url/session/$sid/elements" -ContentType 'application/json' -Body $b -TimeoutSec 30
    return ,@($r.value | ForEach-Object { $_.'element-6066-11e4-a52e-4f735466cecf' })
}
function Click-El($id) { Invoke-RestMethod -Method Post -Uri "$Url/session/$sid/element/$id/click" -ContentType 'application/json' -Body '{}' -TimeoutSec 30 | Out-Null }
function Click-Id($automationId) {
    $els = Find-Els "accessibility id" $automationId
    if ($els.Count -eq 0) { throw "not found: $automationId" }
    Click-El $els[0]
}
function Shot($name) {
    $shot = Invoke-RestMethod -Method Get -Uri "$Url/session/$sid/screenshot" -TimeoutSec 30
    [IO.File]::WriteAllBytes((Join-Path $OutDir $name), [Convert]::FromBase64String($shot.value))
}

Start-Sleep -Seconds 10
[void][CursorProbe]::SetCursorPos(5, 5)
Start-Sleep -Milliseconds 300
$before = [CursorProbe]::Pos()
"cursor parked = $before"

Click-Id "NavProxiesButton"
Start-Sleep -Seconds 18

Click-Id "ProxyStatusFilterHeader"
Start-Sleep -Milliseconds 900
Click-Id "ProxyFilterInvalidItem"
Start-Sleep -Seconds 2
Shot "nomouse-failed.png"

Click-Id "ProxyStatusFilterHeader"
Start-Sleep -Milliseconds 900
Click-Id "ProxyFilterAllItem"
Start-Sleep -Seconds 2

$after = [CursorProbe]::Pos()
"cursor after  = $after"
if ($before -eq $after) { "CURSOR_UNCHANGED=true" } else { "CURSOR_UNCHANGED=false" }

Invoke-RestMethod -Method Delete -Uri "$Url/session/$sid" -TimeoutSec 30 | Out-Null
"DONE"
