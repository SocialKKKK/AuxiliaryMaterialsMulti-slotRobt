# Switch to the 6th tab (the material access log page) and screenshot the main window.
# ASCII-only on purpose: PowerShell 5.1 parses BOM-less UTF-8 scripts as ANSI and would mangle CJK literals.
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
Write-Output ("clickable point: " + [int]$pt.X + "," + [int]$pt.Y)
[void][W32]::SetCursorPos([int]$pt.X, [int]$pt.Y)
Start-Sleep -Milliseconds 300
[W32]::mouse_event(0x0002, 0, 0, 0, [IntPtr]::Zero)
[W32]::mouse_event(0x0004, 0, 0, 0, [IntPtr]::Zero)
Start-Sleep -Milliseconds 1800

$r = New-Object W32+RECT
[void][W32]::GetWindowRect($h, [ref]$r)
$w = $r.Right - $r.Left
$ht = $r.Bottom - $r.Top
Write-Output ("window rect: " + $r.Left + "," + $r.Top + " " + ${w} + "x" + ${ht})

$bmp = New-Object System.Drawing.Bitmap($w, $ht)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
$out = Join-Path (Get-Location) 'oplog-page.png'
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose()
$bmp.Dispose()
Write-Output ("saved: " + $out + " exists=" + (Test-Path $out))
