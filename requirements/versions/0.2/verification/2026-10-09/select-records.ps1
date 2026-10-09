$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$trace='C:\autotest\upgrade-20261009-140134\ui-probe.txt'
('session='+ (Get-Process -Id $PID).SessionId) | Set-Content -LiteralPath $trace -Encoding UTF8
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
$p=Get-Process WireCabinet.Hmi | Select-Object -First 1
$cond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$p.Id)
$windows=[System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children,$cond)
foreach($w in $windows){('window='+$w.Current.Name+' handle='+$w.Current.NativeWindowHandle) | Add-Content -LiteralPath $trace -Encoding UTF8}
foreach($w in $windows){
 $radioCond=New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,'RbOpLog')
 $radio=$w.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$radioCond)
 if($null -eq $radio){continue}
 $sel=$radio.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
 ('before-selected='+$sel.Current.IsSelected+' enabled='+$radio.Current.IsEnabled) | Add-Content -LiteralPath $trace -Encoding UTF8
 $sel.Select()
 'SELECT_RETURNED' | Add-Content -LiteralPath $trace -Encoding UTF8
 break
}

