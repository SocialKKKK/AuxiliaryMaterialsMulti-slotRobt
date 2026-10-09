param([switch]$CheckOnly)
$ErrorActionPreference='Stop'
$Root='C:\Users\HT\Desktop\AuxiliaryMaterialsMulti-slotRobt'
$Backup='C:\autotest\upgrade-20261009-140134\backup'
$Bin='C:\Users\HT\Desktop\AuxiliaryMaterialsMulti-slotRobt\requirements\versions\0.1\wire-cabinet\src\WireCabinet.Hmi\bin\Release\net8.0-windows'
function Safe-Path([string]$Base,[string]$Relative) {
 $baseFull=[IO.Path]::GetFullPath($Base).TrimEnd('\')+'\'
 $full=[IO.Path]::GetFullPath((Join-Path $Base $Relative))
 if(-not $full.StartsWith($baseFull,[StringComparison]::OrdinalIgnoreCase)){throw 'UNSAFE_PATH'}
 return $full
}
if(-not(Test-Path -LiteralPath (Join-Path $Backup 'complete.txt'))){throw 'BACKUP_INCOMPLETE'}
$records=Get-Content -LiteralPath (Join-Path $Backup 'restore-manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach($r in $records){
 $null=Safe-Path $Root $r.path
 if($r.existed){
  $src=Safe-Path $Backup $r.path
  if(-not(Test-Path -LiteralPath $src) -or (Get-FileHash -LiteralPath $src).Hash -ne $r.sha256){throw 'ROLLBACK_SOURCE_INVALID'}
 }
}
'ROLLBACK_CHECK_OK target=pre-upgrade September 0.2; database retained'
if($CheckOnly){return}
Get-CimInstance Win32_Process -Filter "Name='WireCabinet.Hmi.exe'" | Where-Object {$_.ExecutablePath -eq (Join-Path $Bin 'WireCabinet.Hmi.exe')} | ForEach-Object {Stop-Process -Id $_.ProcessId -Force}
Start-Sleep -Seconds 2
foreach($r in $records){
 $target=Safe-Path $Root $r.path
 if($r.existed){
  New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
  Copy-Item -LiteralPath (Safe-Path $Backup $r.path) -Destination $target -Force
 } elseif(Test-Path -LiteralPath $target){
  Remove-Item -LiteralPath $target -Force
 }
}
foreach($r in $records){if($r.existed -and (Get-FileHash -LiteralPath (Safe-Path $Root $r.path)).Hash -ne $r.sha256){throw 'ROLLBACK_COPY_INVALID'}}
Start-ScheduledTask -TaskName WireCabinet-StartHmi
'ROLLBACK_DONE September 0.2 restored; database retained'
