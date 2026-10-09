param([switch]$CheckOnly)
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding
$Root='C:\Users\HT\Desktop\AuxiliaryMaterialsMulti-slotRobt'
$Work='C:\autotest\upgrade-20261009-140134'
$Package=Join-Path $Work 'package'
$Backup=Join-Path $Work 'backup'
$BinRel='requirements\versions\0.1\wire-cabinet\src\WireCabinet.Hmi\bin\Release\net8.0-windows'
$Bin=Join-Path $Root $BinRel
$LocalRel=Join-Path $BinRel 'appsettings.Local.json'
$IoRel='requirements\versions\0.1\wire-cabinet\slot-config\io-modules.yaml'
$Data=Join-Path $Root 'requirements\versions\0.1\wire-cabinet\data'
$Zip=Join-Path $Work 'upgrade-20261009.zip'
if((Get-FileHash -LiteralPath $Zip).Hash -ne 'C39E7F734DE949469CC29806ED3324D81F4CA86026CDD006D2457B8FFA820D0F'){throw 'ZIP_HASH_MISMATCH'}
function Safe-Path([string]$Base,[string]$Relative) {
 $baseFull=[IO.Path]::GetFullPath($Base).TrimEnd('\')+'\'
 $full=[IO.Path]::GetFullPath((Join-Path $Base $Relative))
 if(-not $full.StartsWith($baseFull,[StringComparison]::OrdinalIgnoreCase)){throw 'UNSAFE_PATH'}
 return $full
}
function Merge-Config($Base,$Overlay) {
 if($Base -is [pscustomobject] -and $Overlay -is [pscustomobject]) {
  $result=[ordered]@{}
  foreach($p in $Base.PSObject.Properties){$result[$p.Name]=$p.Value}
  foreach($p in $Overlay.PSObject.Properties){
   if($result.Contains($p.Name)){$result[$p.Name]=Merge-Config $result[$p.Name] $p.Value}
   else{$result[$p.Name]=$p.Value}
  }
  return [pscustomobject]$result
 }
 return ,$Overlay
}
function Read-Json([string]$Path){Get-Content -LiteralPath $Path -Raw -Encoding UTF8 | ConvertFrom-Json}
function Flatten-Config($Value,[string]$Prefix,$Result){
 if($Value -is [pscustomobject]){
  foreach($p in $Value.PSObject.Properties){Flatten-Config $p.Value ($Prefix+':'+$p.Name) $Result}
 } else {
  if($null -eq $Value){$Result[$Prefix]='null'}
  else{$Result[$Prefix]=ConvertTo-Json -InputObject $Value -Depth 30 -Compress}
 }
}
function Hmi-Processes {
 @(Get-CimInstance Win32_Process -Filter "Name='WireCabinet.Hmi.exe'" | Where-Object {
  $_.ExecutablePath -eq (Join-Path $Bin 'WireCabinet.Hmi.exe')
 })
}
if(-not (Test-Path -LiteralPath $Package)){
 New-Item -ItemType Directory -Path $Package | Out-Null
 Expand-Archive -LiteralPath $Zip -DestinationPath $Package
}
$Manifest=Read-Json (Join-Path $Package 'manifest.json')
foreach($f in $Manifest){
 $src=Safe-Path $Package $f.path
 $null=Safe-Path $Root $f.path
 if($f.path -match 'appsettings\.Local\.json|io-modules\.yaml|(^|/)data/|app\.db'){throw 'PACKAGE_CONTAINS_SITE_DATA'}
 if(-not (Test-Path -LiteralPath $src)){throw 'PACKAGE_FILE_MISSING'}
 if((Get-FileHash -LiteralPath $src).Hash -ne $f.sha256){throw 'PACKAGE_FILE_HASH_MISMATCH'}
}
foreach($rel in @(
 'requirements\data-access-sql\sql-catalog\items\app.slot.has_available_lot.yaml',
 'requirements\data-access-sql\sql-catalog\items\app.slot.list_available_lots.yaml',
 'requirements\data-access-sql\sql-catalog\items\mes.wire.clear_disco_eqp_no.yaml',
 'requirements\validation\wire-flow-lab\data-access-sql\sql-catalog\items\mes_mock.wire.clear_disco_eqp_no.yaml'
)){if(-not (Test-Path -LiteralPath (Safe-Path $Package $rel))){throw 'REQUIRED_CATALOG_MISSING'}}
$originalShared=Read-Json (Join-Path $Bin 'appsettings.json')
$originalLocal=Read-Json (Join-Path $Bin 'appsettings.Local.json')
$effective=Merge-Config $originalShared $originalLocal
foreach($v in @($effective.Mes.ConnectionString,$effective.AgvDispatch.BaseUrl,$effective.AgvDispatch.Username,$effective.AgvDispatch.Password,$effective.MaintAccess.Password,$effective.MhAccess.Password)){
 if([string]::IsNullOrWhiteSpace([string]$v)){throw 'SITE_REQUIRED_CONFIG_EMPTY'}
}
if($effective.AgvThresholds.LowBatteryPercent -ne 30 -or $effective.AgvThresholds.FullBatteryPercent -ne 90){throw 'SITE_THRESHOLDS_CHANGED'}
if(-not(Test-Path -LiteralPath (Safe-Path $Root $IoRel))){throw 'SITE_IO_CONFIG_MISSING'}
$processes=@(Hmi-Processes)
if($processes.Count -ne 1){throw 'EXPECTED_ONE_HMI_PROCESS'}
$task=Get-ScheduledTask -TaskName WireCabinet-StartHmi
if($task.Actions.Execute -ne (Join-Path $Bin 'WireCabinet.Hmi.exe')){throw 'START_TASK_TARGET_MISMATCH'}
'PREFLIGHT_OK files='+$Manifest.Count+' thresholds=30/90 site_credentials_present=True'
if($CheckOnly){return}
if(Test-Path -LiteralPath $Backup){throw 'BACKUP_ALREADY_EXISTS'}
New-Item -ItemType Directory -Path $Backup | Out-Null
$records=New-Object System.Collections.ArrayList
$localBeforeHash=(Get-FileHash -LiteralPath (Safe-Path $Root $LocalRel)).Hash
$ioBeforeHash=(Get-FileHash -LiteralPath (Safe-Path $Root $IoRel)).Hash
foreach($rel in @($Manifest | ForEach-Object {$_.path})+@($LocalRel,$IoRel)){
 $dest=Safe-Path $Root $rel
 $exists=Test-Path -LiteralPath $dest
 $hash=$null
 if($exists){
  $hash=(Get-FileHash -LiteralPath $dest).Hash
  $target=Safe-Path $Backup $rel
  New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
  Copy-Item -LiteralPath $dest -Destination $target
  if((Get-FileHash -LiteralPath $target).Hash -ne $hash){throw 'BACKUP_HASH_MISMATCH'}
 }
 [void]$records.Add([pscustomobject]@{path=$rel;existed=$exists;sha256=$hash})
}
$records | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Backup 'restore-manifest.json') -Encoding UTF8
$effective | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $Backup 'effective-before.json') -Encoding UTF8
$rollbackScript=Join-Path $Work 'rollback.ps1'
if(-not(Test-Path -LiteralPath $rollbackScript)){throw 'ROLLBACK_SCRIPT_MISSING'}
$stopped=$false
$applied=$false
try {
 foreach($p in $processes){Stop-Process -Id $p.ProcessId -Force}
 $stopped=$true
 Start-Sleep -Seconds 2
 if(@(Hmi-Processes).Count -ne 0){throw 'HMI_STILL_RUNNING'}
 $dbBackup=Join-Path $Backup 'database'
 New-Item -ItemType Directory -Path $dbBackup | Out-Null
 $dbFiles=@(Get-ChildItem -LiteralPath $Data -File | Where-Object {$_.Name -in @('app.db','app.db-wal','app.db-shm')})
 if(-not($dbFiles | Where-Object {$_.Name -eq 'app.db'})){throw 'DATABASE_MISSING'}
 foreach($db in $dbFiles){
  Copy-Item -LiteralPath $db.FullName -Destination (Join-Path $dbBackup $db.Name)
  if((Get-FileHash -LiteralPath $db.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $dbBackup $db.Name)).Hash){throw 'DATABASE_BACKUP_HASH_MISMATCH'}
 }
 $dataBackup=Join-Path $Data 'backup\upgrade-20261009-140134'
 New-Item -ItemType Directory -Path $dataBackup | Out-Null
 foreach($db in $dbFiles){Copy-Item -LiteralPath (Join-Path $dbBackup $db.Name) -Destination (Join-Path $dataBackup $db.Name)}
 Set-Content -LiteralPath (Join-Path $Backup 'complete.txt') -Value 'BACKUP_COMPLETE' -Encoding ascii
 'BACKUP_COMPLETE path='+$Backup
 $applied=$true
 $effective | ConvertTo-Json -Depth 40 | Set-Content -LiteralPath (Join-Path $Bin 'appsettings.Local.json') -Encoding UTF8
 foreach($f in $Manifest){
  $target=Safe-Path $Root $f.path
  New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
  Copy-Item -LiteralPath (Safe-Path $Package $f.path) -Destination $target -Force
 }
 foreach($f in $Manifest){
  if((Get-FileHash -LiteralPath (Safe-Path $Root $f.path)).Hash -ne $f.sha256){throw 'DEPLOYED_HASH_MISMATCH'}
 }
 if((Get-FileHash -LiteralPath (Safe-Path $Root $IoRel)).Hash -ne $ioBeforeHash){throw 'IO_CONFIG_CHANGED'}
 $nowEffective=Merge-Config (Read-Json (Join-Path $Bin 'appsettings.json')) (Read-Json (Join-Path $Bin 'appsettings.Local.json'))
 $before=@{}; $after=@{}
 Flatten-Config $effective '' $before
 Flatten-Config $nowEffective '' $after
 foreach($key in $before.Keys){if(-not $after.ContainsKey($key) -or $after[$key] -ne $before[$key]){throw 'EFFECTIVE_CONFIG_CHANGED'}}
 $newShared=Read-Json (Join-Path $Bin 'appsettings.json')
 foreach($v in @($newShared.Mes.ConnectionString,$newShared.AgvDispatch.BaseUrl,$newShared.AgvDispatch.Username,$newShared.AgvDispatch.Password,$newShared.MaintAccess.Password,$newShared.MhAccess.Password)){
  if(-not [string]::IsNullOrWhiteSpace([string]$v)){throw 'SHARED_CONFIG_CONTAINS_SITE_SECRET'}
 }
 Copy-Item -LiteralPath $rollbackScript -Destination C:\autotest\deploy-rollback-20261009.ps1 -Force
 Start-ScheduledTask -TaskName WireCabinet-StartHmi
 Start-Sleep -Seconds 12
 $newProcesses=@(Hmi-Processes)
 if($newProcesses.Count -ne 1){throw 'NEW_HMI_NOT_RUNNING'}
 $receipt=[pscustomobject]@{
  deployedAt=(Get-Date -Format o);sourceCommit='697d085';files=$Manifest.Count;
  pid=$newProcesses[0].ProcessId;backup=$Backup;databaseBackup=$dataBackup;
  hmiHash=(Get-FileHash -LiteralPath (Join-Path $Bin 'WireCabinet.Hmi.dll')).Hash;
  rcsHash=(Get-FileHash -LiteralPath (Join-Path $Bin 'WireCabinet.Rcs.dll')).Hash;
  effectiveConfigPreserved=$true;ioConfigPreserved=$true;thresholds='30/90';
  rollback='C:\autotest\deploy-rollback-20261009.ps1'
 }
 $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Work 'receipt.json') -Encoding UTF8
 $receipt | ConvertTo-Json -Depth 5
 'UPGRADE_APPLIED'
} catch {
 'UPGRADE_FAILED '+$_.Exception.Message
 if($applied){
  & $rollbackScript
 } elseif($stopped){
  Start-ScheduledTask -TaskName WireCabinet-StartHmi
 }
 throw
}
