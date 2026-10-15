<#
.SYNOPSIS
  Cursor-free UI Automation helper for Windows desktop apps (no physical mouse/keyboard).

.DESCRIPTION
  Instead of SendInput (which moves the real cursor and steals focus), this uses
  UI Automation patterns (Invoke/Toggle/Select/Expand) and, as a fallback,
  Win32 PostMessage clicks. The automation never touches your physical mouse.

  Run with Windows PowerShell (powershell.exe), not pwsh.

.EXAMPLE
  powershell.exe -File uia.ps1 -Action list -ProcessName YellowFox.Desktop
  powershell.exe -File uia.ps1 -Action invoke -ProcessName YellowFox.Desktop -AutomationId NavProxiesButton
  powershell.exe -File uia.ps1 -Action setvalue -ProcessName YellowFox.Desktop -AutomationId ProxySearchBox -Value hello
  powershell.exe -File uia.ps1 -Action get -ProcessName YellowFox.Desktop -AutomationId ProxiesList
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('list', 'get', 'invoke', 'setvalue', 'select', 'expand', 'collapse', 'toggle', 'focus')]
    [string]$Action,

    [string]$ProcessName,
    [string]$ProcessId,

    [string]$AutomationId,
    [string]$Name,
    [string]$ClassName,

    [string]$Value,
    [int]$Index = 0,

    [switch]$Json
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Resolve-ProcessId {
    if ($ProcessId) { return [int]$ProcessId }
    if (-not $ProcessName) { throw 'Provide -ProcessName or -ProcessId' }
    $procs = @(Get-Process -Name $ProcessName -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 })
    if ($procs.Count -eq 0) { throw "No windowed process named '$ProcessName'" }
    return [int]$procs[0].Id
}

function Get-RootWindow {
    param([int]$Pid_)
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Pid_)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children, $cond)
}

function New-TargetCondition {
    $conds = @()
    if ($AutomationId) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    }
    if ($Name) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $Name)
    }
    if ($ClassName) {
        $conds += New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ClassNameProperty, $ClassName)
    }
    if ($conds.Count -eq 0) { throw 'Provide at least one of -AutomationId/-Name/-ClassName' }
    if ($conds.Count -eq 1) { return $conds[0] }
    return New-Object System.Windows.Automation.AndCondition($conds)
}

function Find-Target {
    param($Window, $Condition)
    $all = $Window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $Condition)
    if ($all.Count -eq 0) { throw 'Element not found' }
    if ($Index -ge $all.Count) { throw "Index $Index out of range (found $($all.Count))" }
    return $all[$Index]
}

function Get-ElementInfo {
    param($El)
    $r = $El.Current.BoundingRectangle
    $patterns = @()
    foreach ($p in @('Invoke', 'Toggle', 'SelectionItem', 'ExpandCollapse', 'Value', 'ScrollItem', 'Text')) {
        if (Test-Pattern -El $El -PatternName $p) { $patterns += $p }
    }
    [pscustomobject]@{
        Name         = $El.Current.Name
        AutomationId = $El.Current.AutomationId
        ClassName    = $El.Current.ClassName
        ControlType  = $El.Current.ControlType.ProgrammaticName
        Enabled      = $El.Current.IsEnabled
        Offscreen    = $El.Current.IsOffscreen
        X            = [int]$r.X
        Y            = [int]$r.Y
        Width        = [int]$r.Width
        Height       = [int]$r.Height
        Patterns     = ($patterns -join ',')
    }
}

function Test-Pattern {
    param($El, [string]$PatternName)
    try {
        switch ($PatternName) {
            'Invoke' { $null = $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern) }
            'Toggle' { $null = $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern) }
            'SelectionItem' { $null = $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern) }
            'ExpandCollapse' { $null = $El.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern) }
            'Value' { $null = $El.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern) }
            'ScrollItem' { $null = $El.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern) }
            'Text' { $null = $El.GetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern) }
            default { return $false }
        }
        return $true
    } catch { return $false }
}

function Invoke-Element {
    param($El)
    if (Test-Pattern -El $El -PatternName 'Invoke') {
        $El.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); return 'Invoke'
    }
    if (Test-Pattern -El $El -PatternName 'Toggle') {
        $El.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); return 'Toggle'
    }
    if (Test-Pattern -El $El -PatternName 'SelectionItem') {
        $El.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); return 'SelectionItem'
    }
    if (Test-Pattern -El $El -PatternName 'ExpandCollapse') {
        $El.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); return 'ExpandCollapse'
    }
    return (Invoke-Win32Click $El)
}

# Fallback: WM_LBUTTONDOWN/UP to the element's own window at its centre.
# Does not move the cursor. Works for many native controls, may fail for some.
function Invoke-Win32Click {
    param($El)
    if (-not ('W.U32' -as [type])) {
        Add-Type -Namespace W -Name U32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
[DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
[DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
[DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
[StructLayout(LayoutKind.Sequential)] public struct POINT { public int X; public int Y; }
'@
    }
    # Find the window actually under the element's centre (works for popups
    # like MenuFlyout, which live in their own top-level window). No cursor move.
    $r = $El.Current.BoundingRectangle
    $pt = New-Object 'W.U32+POINT'
    $pt.X = [int]($r.X + $r.Width / 2)
    $pt.Y = [int]($r.Y + $r.Height / 2)
    $hit = [W.U32]::WindowFromPoint($pt)
    if ($hit -eq [IntPtr]::Zero) { return 'none' }
    $root = [W.U32]::GetAncestor($hit, 2) # GA_ROOT
    $hwnd = if ($root -ne [IntPtr]::Zero) { $root } else { $hit }
    [void][W.U32]::ScreenToClient($hwnd, [ref]$pt)
    $lparam = [IntPtr](($pt.Y -shl 16) -bor ($pt.X -band 0xFFFF))
    [void][W.U32]::PostMessage($hwnd, 0x0201, [IntPtr]1, $lparam) # WM_LBUTTONDOWN
    Start-Sleep -Milliseconds 30
    [void][W.U32]::PostMessage($hwnd, 0x0202, [IntPtr]0, $lparam) # WM_LBUTTONUP
    return 'PostMessage'
}

# ---- main ----

$targetPid = Resolve-ProcessId
$window = Get-RootWindow -Pid_ $targetPid
if ($null -eq $window) { throw "Window not found for PID $targetPid" }

if ($Action -eq 'list') {
    $cond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::IsControlElementProperty, $true)
    $found = $window.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    $rows = foreach ($e in $found) {
        if ($e.Current.AutomationId -or $e.Current.Name) {
            try { Get-ElementInfo $e } catch { }
        }
    }
    if ($Json) { $rows | ConvertTo-Json -Compress } else { $rows | Format-Table -AutoSize }
    return
}

$element = Find-Target -Window $window -Condition (New-TargetCondition)

switch ($Action) {
    'get' {
        $info = Get-ElementInfo $element
        if ($Json) { $info | ConvertTo-Json -Compress } else { $info | Format-List }
    }
    'invoke' {
        $used = Invoke-Element $element
        [pscustomobject]@{ Action = 'invoke'; Method = $used; AutomationId = $element.Current.AutomationId; Name = $element.Current.Name } |
            ConvertTo-Json -Compress
    }
    'toggle' {
        $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle(); 'toggled'
    }
    'select' {
        $element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); 'selected'
    }
    'expand' {
        $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); 'expanded'
    }
    'collapse' {
        $element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse(); 'collapsed'
    }
    'focus' {
        $element.SetFocus(); 'focused'
    }
    'setvalue' {
        if ($null -eq $Value) { throw 'Provide -Value' }
        $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value); 'value set'
    }
}
