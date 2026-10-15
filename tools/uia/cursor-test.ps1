param(
    [string]$ProcessName = 'YellowFox.Desktop',
    [string]$AutomationId = 'ProxiesRefreshButton',
    [string]$UiaScript = 'D:\YandexDisk\Coding\Arbitrazh\YellowFox\tools\uia\uia.ps1'
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

# Park the cursor away from the app, then invoke. If the click were physical,
# it would jump to the element's rectangle (see -Action get output).
[void][CursorProbe]::SetCursorPos(10, 10)
Start-Sleep -Milliseconds 300
$before = [CursorProbe]::Pos()
"cursor parked = $before"

$result = & $UiaScript -Action invoke -ProcessName $ProcessName -AutomationId $AutomationId
"invoke result = $result"
Start-Sleep -Milliseconds 1200

$after = [CursorProbe]::Pos()
"cursor after  = $after"
if ($before -eq $after) { "CURSOR_UNCHANGED=true" } else { "CURSOR_UNCHANGED=false (physical click would move it to the element)" }
