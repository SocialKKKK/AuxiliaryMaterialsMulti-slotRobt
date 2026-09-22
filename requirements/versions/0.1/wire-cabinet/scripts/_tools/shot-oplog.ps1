# Switch to the 6th tab (the material access log page) and screenshot the main window.
# Optionally click a toolbar button first, to capture a specific mode.
#   -Click pending   -> click the pending-compensation button
#   -Out <file>      -> output png name (relative to the current directory)
# ASCII-only on purpose: PowerShell 5.1 parses BOM-less UTF-8 scripts as ANSI and would mangle CJK literals.
param([string]$Click = 'none', [string]$Out = 'oplog-page.png')

try { Add-Type -AssemblyName System.Drawing } catch { Write-Output ("AddType Drawing failed: " + $_.Exception.Message) }
try { Add-Type -AssemblyName UIAutomationClient } catch { Write-Output ("AddType UIA failed: " + $_.Exception.Message) }
try { Add-Type -AssemblyName UIAutomationTypes } catch { }

$sig = @'
using System;
using System.Runtime.InteropServices;
public class W32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint x, uint y, uint d, IntPtr e);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    public struct RECT { public int Left, Top, Right, Bottom; }
}
'@
Add-Type -TypeDefinition $sig

$proc = Get-Process WireCabinet.Hmi -ErrorAction Stop | Select-Object -First 1
$h = $proc.MainWindowHandle
[void][W32]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 900

# U+5B58 U+53D6 U+8BB0 U+5F55 = the 4-character tab label
$tabName = [string]::Join('', [char]0x5B58, [char]0x53D6, [char]0x8BB0, [char]0x5F55)
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $tabName)
$tab = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if ($null -eq $tab) { Write-Output 'FAIL: tab element not found'; exit 1 }
Write-Output ("found tab, label length = " + $tab.Current.Name.Length)

$pt = $tab.GetClickablePoint()
[void][W32]::SetCursorPos([int]$pt.X, [int]$pt.Y)
Start-Sleep -Milliseconds 300
[W32]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
[W32]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1800

if ($Click -ne 'none') {
    # Button labels carry CJK, so build the match string from code points:
    #   pending  -> U+5F85 U+8865 U+507F  "待补偿"      (has a count suffix, match by prefix)
    #   retryall -> U+5168 U+90E8 U+91CD U+8BD5  "全部重试" (exact)
    switch ($Click) {
        'pending'  { $label = [string]::Join('', [char]0x5F85, [char]0x8865, [char]0x507F); $exact = $false }
        'retryall' { $label = [string]::Join('', [char]0x5168, [char]0x90E8, [char]0x91CD, [char]0x8BD5); $exact = $true }
        default    { Write-Output ("FAIL: unknown -Click " + $Click); exit 1 }
    }

    $btnCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::Button)
    $buttons = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $btnCond)
    $target = $null
    foreach ($b in $buttons) {
        $n = $b.Current.Name
        if ($exact) { if ($n -eq $label) { $target = $b; break } }
        else { if ($n -like ($label + '*')) { $target = $b; break } }
    }
    if ($null -eq $target) { Write-Output ("FAIL: button not found for " + $Click); exit 1 }

    Write-Output ("clicking button: " + $target.Current.Name)
    $inv = $target.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $inv.Invoke()
    Start-Sleep -Milliseconds 1800
}

$r = New-Object W32+RECT
[void][W32]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Write-Output ("window rect: " + $r.Left + "," + $r.Top + " " + ${w} + "x" + ${ht})

# PrintWindow (PW_RENDERFULLCONTENT) 让窗口把自身渲染到位图，而不是按屏幕坐标拷贝。
# 后者在窗口未置顶时会抓到该位置上的**别的窗口**（包括用户桌面内容），所以不用。
$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$hdc = $g.GetHdc()
try { [void][W32]::PrintWindow($h, $hdc, 2) } finally { $g.ReleaseHdc($hdc) }
$path = Join-Path (Get-Location) $Out
$bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Output ("saved: " + $path + " exists=" + (Test-Path $path))
