# Runs the current updater with synthetic files and mocked host adapters only.
# Never registers tasks, touches Windows services, executes an EXE, or uses SQL/network.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$work=Join-Path $repo ('.tools\sync-upgrade-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work|Out-Null
$release=Join-Path $work 'release';$scripts=Join-Path $release 'scripts'
New-Item -ItemType Directory -Force $scripts,(Join-Path $release 'app')|Out-Null
$source=(Get-Content -LiteralPath (Join-Path $repo 'deploy\windows\update.ps1') -Raw) -replace '(?m)^#Requires -RunAsAdministrator\r?\n',''
[IO.File]::WriteAllText((Join-Path $scripts 'update.ps1'),$source)
Copy-Item -LiteralPath (Join-Path $repo 'deploy\windows\sync-upgrade-common.ps1') -Destination $scripts
[IO.File]::WriteAllText((Join-Path $release 'app\AlShifa.Api.exe'),'synthetic-new-app-never-executed')
[IO.File]::WriteAllText((Join-Path $scripts 'runtime-common.ps1'),'function Ensure-VisualCppRuntime {}')
[IO.File]::WriteAllText((Join-Path $scripts 'upgrade-safety.ps1'),@'
function Assert-UpgradeEnvironment {}
function Get-UpgradeDataProof { @{layout=@();values=@('synthetic-proof')} }
'@)
[IO.File]::WriteAllText((Join-Path $scripts 'common.ps1'),@'
$ErrorActionPreference='Stop'
function Get-CimInstance($ClassName,$Filter){
 if($Filter -like '*AlShifaApi*'){[pscustomobject]@{PathName=(Join-Path $global:SyncTestRoot 'app\AlShifa.Api.exe')}}
 else{[pscustomobject]@{PathName=((Join-Path $global:SyncTestRoot 'mysql\bin\mysqld.exe')+' --synthetic')}}
}
function Protect-Path {if($global:SyncTestCase -eq 'failed-copy'){throw 'Synthetic copy-stage failure'}}
function Start-Sleep {}
function Add-BackupRequestTrigger {}
function Set-NightlyBackupSchedule {}
function Set-AutoUpdateTasks {}
function Stop-Service {$global:SyncTestRunning=$false}
function Start-Service {$global:SyncTestRunning=$true}
function Restart-Service {$global:SyncTestRunning=$true;$global:SyncTestRestarts++}
function Restore-AlShifaDatabaseFromDump {$global:SyncTestRollbacks++}
function Invoke-WebRequest {throw 'NETWORK MUST NEVER BE USED BY ESTABLISHED UPGRADE'}
function Invoke-Native($Executable,[string[]]$Arguments){
 if($Arguments -contains 'SELECT @@port, @@datadir;'){return "33383`t$global:SyncTestRoot\data\"}
 if((Split-Path $Executable -Leaf) -eq 'mysqldump.exe'){
  $target=@($Arguments|Where-Object {$_ -like '--result-file=*'})
  if($target.Count -ne 1){throw 'Synthetic dump target is required'}
  [IO.File]::WriteAllText($target[0].Substring('--result-file='.Length),'-- Dump completed on synthetic fixture')
  return
 }
 if($Arguments -contains '--migrate'){if($global:SyncTestCase -eq 'failed-migration'){throw 'Synthetic migration failure'};return}
 if($Arguments -contains '-Maintenance'){
  $global:SyncTestHealth++
  $current=Get-Content (Join-Path $global:SyncTestRoot 'app\appsettings.Production.json') -Raw|ConvertFrom-Json
  if(-not (Test-Path (Join-Path $global:SyncTestRoot 'keys\update-maintenance')) -and $global:SyncTestRollbacks -eq 0){throw 'Maintenance barrier lifted before final health'}
  if($global:SyncTestHealth -eq 1 -and -not $current.Sync.RequireCompatibilityApproval){throw 'Worker not held during first health check'}
  if($global:SyncTestCase -eq 'failed-health' -and $global:SyncTestHealth -eq 1){throw 'Synthetic health failure'}
  if($global:SyncTestCase -eq 'changed-identity' -and $global:SyncTestHealth -eq 1){[IO.File]::WriteAllText((Join-Path $global:SyncTestRoot 'keys\sync-installation-id'),'22222222-2222-4222-8222-222222222222')}
  if($global:SyncTestCase -eq 'changed-key' -and $global:SyncTestHealth -eq 1){$current.Sync.Key='changed-synthetic-key-should-be-rejected';$current|ConvertTo-Json -Depth 32|Set-Content (Join-Path $global:SyncTestRoot 'app\appsettings.Production.json')}
  if($global:SyncTestCase -eq 'failed-resume-health' -and $global:SyncTestHealth -eq 2){throw 'Synthetic resumed process failure'}
  return
 }
 throw 'Unexpected native command in synthetic update adapter'
}
'@)
[IO.File]::WriteAllText((Join-Path $scripts 'backup.ps1'),@'
param($Root,$Destination,[switch]$AllowSameDisk)
$file=Join-Path $Root 'synthetic-backup.gpg';[IO.File]::WriteAllText($file,'synthetic')
@{restoreVerifiedAt='synthetic';restoreVerifiedFile=(Split-Path $file -Leaf)}|ConvertTo-Json|Set-Content (Join-Path $Root 'logs\backup-status.json')
"Backup complete: $file"
'@)
[IO.File]::WriteAllText((Join-Path $scripts 'db-integrity.ps1'),@'
function Test-AlShifaDatabaseIntegrity { if($global:SyncTestCase -eq 'damaged-database'){@('synthetic: Assertion failure: fsp0fsp.cc:3694')}else{@()} }
'@)
[IO.File]::WriteAllText((Join-Path $scripts 'restore-test.ps1'),"param(`$Backup,`$Root,`$CandidateApp,`$ExpectedSourceProof)`n'MIGRATION-SECONDS: 0'")
[IO.File]::WriteAllText((Join-Path $scripts 'sync-bootstrap.ps1'),"throw 'ESTABLISHED SOURCE MUST NEVER ENTER ENROLLMENT'")

$cases=@('installer-offline','auto-update-offline','damaged-database','failed-copy','failed-migration','failed-health','failed-resume-health','changed-key','changed-identity','missing-identity')
foreach($case in $cases){
 $global:SyncTestCase=$case;$global:SyncTestRoot=Join-Path $work $case
 $global:SyncTestHealth=0;$global:SyncTestRestarts=0;$global:SyncTestRollbacks=0;$global:SyncTestRunning=$true
 foreach($dir in 'app','keys','data','logs','secrets','scripts','mysql\bin'){New-Item -ItemType Directory -Force (Join-Path $global:SyncTestRoot $dir)|Out-Null}
 $identityPath=Join-Path $global:SyncTestRoot 'keys\sync-installation-id'
 [IO.File]::WriteAllText($identityPath,'11111111-1111-4111-8111-111111111111')
 $identityHash=(Get-FileHash $identityPath).Hash
 if($case -eq 'missing-identity'){Remove-Item -LiteralPath $identityPath}
 $config=@{ConnectionStrings=@{Default='Server=127.0.0.1;Port=33383;Database=alshifa;User=synthetic;Password=synthetic'};DataProtection=@{KeysPath=(Join-Path $global:SyncTestRoot 'keys')};Mirror=@{Enabled=$false};Sync=@{Url='https://shafa.novatechsoft.com';Key=('K'*40);RequireCompatibilityApproval=$false;IntervalMinutes=2;StartDelaySeconds=9;CustomSetting='keep'};PreserveOther='keep'}
 $originalSync=$config.Sync|ConvertTo-Json -Depth 32 -Compress
 # Roundtrip before comparing so hashtable/PSObject ordering is identical.
 $originalSync=($originalSync|ConvertFrom-Json)|ConvertTo-Json -Depth 32 -Compress
 $configFile=Join-Path $global:SyncTestRoot 'app\appsettings.Production.json'
 $config|ConvertTo-Json -Depth 32|Set-Content $configFile -Encoding UTF8
 [IO.File]::WriteAllText((Join-Path $global:SyncTestRoot 'app\AlShifa.Api.exe'),'synthetic-old-app-never-executed')
 [IO.File]::WriteAllText((Join-Path $global:SyncTestRoot 'secrets\backup.cnf'),"host=127.0.0.1`nport=33383")
 $history=Join-Path $global:SyncTestRoot 'data\sync-history-sentinel.json'
 [IO.File]::WriteAllText($history,'{"push.seq":12,"push.ackedSeq":10,"push.snapshotOf":7,"push.enabled":false,"pending":[11,12],"receipts":["old"]}')
 $historyHash=(Get-FileHash $history).Hash
 $global:LASTEXITCODE=0;$failed=$false
 try{& (Join-Path $scripts 'update.ps1') -Root $global:SyncTestRoot -BackupDestination (Join-Path $global:SyncTestRoot 'archives') -HoldOnlineServices:($case -ne 'auto-update-offline')|Out-Null}catch{$failed=$true}
 $expectedFailure=$case -notin @('installer-offline','auto-update-offline')
 if($failed -ne $expectedFailure){throw "Unexpected updater result: $case"}
 $after=Get-Content $configFile -Raw|ConvertFrom-Json
 if(($after.Sync|ConvertTo-Json -Depth 32 -Compress) -cne $originalSync -or $after.PreserveOther -ne 'keep'){throw "Existing configuration changed: $case"}
 if((Get-FileHash $history).Hash -ne $historyHash){throw 'Checkpoint, pause state, queue or receipt was changed'}
 if($case -notin @('changed-identity','missing-identity') -and (Get-FileHash $identityPath).Hash -ne $identityHash){throw 'Installation identity changed'}
 if($case -eq 'missing-identity' -and (Test-Path $identityPath)){throw 'Existing identity was silently regenerated'}
 if($case -eq 'changed-identity' -and $global:SyncTestRunning){throw 'Service resumed under a different identity'}
 if($case -eq 'damaged-database'){
  if((Get-Content (Join-Path $global:SyncTestRoot 'app\AlShifa.Api.exe') -Raw) -ne 'synthetic-old-app-never-executed' -or (Test-Path (Join-Path $global:SyncTestRoot 'synthetic-backup.gpg')) -or $global:SyncTestRollbacks -ne 0){throw 'A damaged database was backed up over, migrated or restored'}
  if(-not $global:SyncTestRunning -or (Test-Path (Join-Path $global:SyncTestRoot 'keys\update-maintenance'))){throw 'The old version was not left running after refusing a damaged database'}
 }
 if(-not $expectedFailure){
  $status=Get-Content (Join-Path $global:SyncTestRoot 'logs\sync-bootstrap-status.json') -Raw|ConvertFrom-Json
  if(-not $status.ExistingConfigurationPreserved -or $after.Sync.RequireCompatibilityApproval -or $global:SyncTestRestarts -ne 1 -or $global:SyncTestHealth -ne 2){throw 'Established worker was not restored after health'}
 }
 "PASS: $case; existing settings and sync history preserved."
}
@{passed=$true;cases=$cases.Count;currentUpdaterExecuted=$true;networkUsed=$false;realDatabaseUsed=$false;realServicesTouched=$false;identityAndCheckpointsPreserved=$true;pausedStatePreserved=$true;installerAndAutomaticPaths=$true}|ConvertTo-Json|Set-Content (Join-Path $work 'result.json')
"Synthetic upgrade preservation evidence: $work"
