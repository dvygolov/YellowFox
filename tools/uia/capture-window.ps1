param(
    [string]$ProcessName = 'YellowFox.Desktop',
    [string]$Output = 'D:\YandexDisk\Coding\Arbitrazh\YellowFox\uia-shot.png'
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

$proc = Get-Process -Name $ProcessName | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$proc.Id)
$w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
    [System.Windows.Automation.TreeScope]::Children, $cond)
$r = $w.Current.BoundingRectangle
$x = [int]$r.X; $y = [int]$r.Y; $cx = [int]$r.Width; $cy = [int]$r.Height

$bmp = [System.Drawing.Bitmap]::new($cx, $cy)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, [System.Drawing.Size]::new($cx, $cy))
$bmp.Save($Output, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"saved $Output ($cx x $cy)"
