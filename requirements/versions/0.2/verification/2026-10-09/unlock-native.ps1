$ErrorActionPreference='Stop'
trap {
    Set-Content -LiteralPath C:\autotest\upgrade-20261009-140134\gate-unlock-error.txt -Value $_.Exception.Message -Encoding UTF8
    exit 1
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class GateWin {
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls,string title);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
$title=-join ([char[]](23384,21462,35760,24405,39564,35777))
$hmi=Get-Process WireCabinet.Hmi | Select-Object -First 1
$pidCondition=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$hmi.Id)
$windows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$pidCondition)
$dialog=$null
foreach($window in $windows){if($window.Current.Name -eq $title){$dialog=$window;break}}
if($null -eq $dialog){throw 'EXPECTED_PASSWORD_DIALOG_MISSING'}
$hwnd=[IntPtr]$dialog.Current.NativeWindowHandle
[uint32]$ownerPid=0
[void][GateWin]::GetWindowThreadProcessId($hwnd,[ref]$ownerPid)
if((Get-Process -Id $ownerPid).ProcessName -ne 'WireCabinet.Hmi'){throw 'DIALOG_PROCESS_MISMATCH'}
$cond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'PasswordBox')
$box=$dialog.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$cond)
if($null -eq $box){throw 'PASSWORD_BOX_MISSING'}
[void][GateWin]::SetForegroundWindow($hwnd)
$box.SetFocus()
Start-Sleep -Milliseconds 200
if([GateWin]::GetForegroundWindow() -ne $hwnd){throw 'DIALOG_FOCUS_FAILED'}
$config=Get-Content -LiteralPath 'C:\Users\HT\Desktop\AuxiliaryMaterialsMulti-slotRobt\requirements\versions\0.1\wire-cabinet\src\WireCabinet.Hmi\bin\Release\net8.0-windows\appsettings.Local.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$password=$config.MaintAccess.Password
if($password -notmatch '^[A-Za-z0-9]+$'){throw 'UNEXPECTED_PASSWORD_CHARACTER_SET'}
[System.Windows.Forms.SendKeys]::SendWait('^a')
[System.Windows.Forms.SendKeys]::SendWait($password)
[System.Windows.Forms.SendKeys]::SendWait('{ENTER}')
Start-Sleep -Milliseconds 1000
$windows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$pidCondition)
foreach($window in $windows){if($window.Current.Name -eq $title){throw 'PASSWORD_GATE_NOT_CLOSED'}}
Set-Content -LiteralPath C:\autotest\upgrade-20261009-140134\gate-unlock-result.txt -Value 'GATE_UNLOCK_OK' -Encoding ascii

