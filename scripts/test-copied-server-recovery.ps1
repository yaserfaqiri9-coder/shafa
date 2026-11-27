# Execute the current entry script with synthetic files and mocked Windows/SQL
# adapters. Never registers a service/task, executes an EXE, imports a real SQL
# dump, modifies certificate stores/firewall/registry, or contacts the network.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$work=Join-Path $repo ('.tools\recovery-flow-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work|Out-Null
$package=Join-Path $work 'package';$scripts=Join-Path $package 'scripts';$source=Join-Path $work 'source'
New-Item -ItemType Directory -Force $scripts,(Join-Path $package 'tools')|Out-Null
Copy-Item -Path (Join-Path $repo 'deploy\windows\*.ps1'),(Join-Path $repo 'deploy\windows\*.json') -Destination $scripts
$entry=Join-Path $scripts 'recover-copied-server.ps1'
$code=[IO.File]::ReadAllText($entry)
$code=$code -replace '(?m)^if\(-not\(\[Security\.Principal\.WindowsPrincipal\].*Administrator.*\r?\n',''
$code=$code -replace '(?m)^    if\(-not\[Diagnostics\.EventLog\]::SourceExists.*\r?\n',''
[IO.File]::WriteAllText($entry,$code)
[IO.File]::WriteAllText((Join-Path $scripts 'backup.ps1'),@'
param($Root,$Destination,[switch]$AllowSameDisk)
if($global:RecoveryCase -eq 'failed-backup'){throw 'Synthetic backup failure'}
@{restoreVerifiedAt='synthetic-verified';lastError=$null}|ConvertTo-Json|Set-Content (Join-Path $Root 'logs\backup-status.json')
'@)
Add-Content -LiteralPath (Join-Path $scripts 'common.ps1') -Value @'
function Get-Service {}
function Get-ScheduledTask {}
function Test-PortFree { $true }
function Protect-Path {}
function Find-Gpg { 'synthetic-gpg-never-executed' }
function Unprotect-BackupFile($Gpg,$File,$OutputFile,$PasswordFile,$WorkDir){[IO.File]::WriteAllText($OutputFile,'synthetic SQL never imported')}
function Start-Sleep {}
function Get-CimInstance($ClassName,$Filter) {
 if($Filter -like '*AlShifaApi*'){return [pscustomobject]@{PathName=(Join-Path $global:RecoveryRoot 'app\AlShifa.Api.exe')}}
 if($Filter -like '*AlShifaMySQL*'){return [pscustomobject]@{PathName=((Join-Path $global:RecoveryRoot 'mysql\bin\mysqld.exe')+' --synthetic')}}
 [pscustomobject]@{TotalVisibleMemorySize=4MB}
}
function New-ItemProperty {}
function Start-Service($Name){
 if($Name -eq 'AlShifaApi'){
  $cfg=Get-Content (Join-Path $global:RecoveryRoot 'app\appsettings.Production.json') -Raw|ConvertFrom-Json
  if(-not $cfg.Sync.RequireCompatibilityApproval){throw 'Sync started before health/compatibility check'}
 }
}
function Stop-Service { $global:RecoveryStops++ }
function Restart-Service { $global:RecoveryRestarts++ }
function Invoke-WebRequest {
 if($global:RecoveryCase -eq 'failed-health'){throw 'Synthetic unhealthy application'}
 if($global:RecoveryCase -eq 'failed-resume' -and $global:RecoveryRestarts -eq 1){throw 'Synthetic resumed health failure'}
 [pscustomobject]@{Content='Healthy'}
}
function New-ScheduledTaskSettingsSet { [pscustomobject]@{} }
function New-ScheduledTaskTrigger { [pscustomobject]@{} }
function New-ScheduledTaskAction { [pscustomobject]@{} }
function New-KabulBackupTrigger { [pscustomobject]@{} }
function Register-ScheduledTask { $global:RecoveryTasks++ }
function Unregister-ScheduledTask {}
function Add-BackupRequestTrigger {}
function New-NetFirewallRule {}
function Import-Certificate {}
function Install-AlShifaLauncher {}
function Set-AutoUpdateTasks { $global:RecoveryUpdateTasks++ }
function Invoke-Native($Exe,[string[]]$Arguments){
 if($Arguments -contains 'SELECT @@port,@@datadir;'){return "33494`t$global:RecoveryRoot\data\"}
 if((Split-Path $Exe -Leaf) -eq 'mysql.exe'){
  $q=$Arguments[-1]
  if($q -like 'SELECT TABLE_NAME FROM*'){return 'Patients'}
  if($q -like 'SELECT TABLE_NAME,COLUMN_NAME*'){return @("Patients`tId","Patients`tName")}
  if($q -like "SELECT 'Patients',COUNT(*)*"){return "Patients`t0`t0`t0`t0`t0"}
  if($q -like 'CHECK TABLE*'){return "alshifa.Patients`tcheck`tstatus`tOK"}
  if($q -like 'SELECT (SELECT MigrationId*'){return "synthetic-schema`t10`t12"}
  if($q -like 'source *'){$global:RecoveryImports++;if($global:RecoveryCase -eq 'failed-import'){throw 'Synthetic import failure'};return}
  if($q -eq 'SELECT 1'){return '1'}
  throw 'Unexpected SQL in synthetic recovery'
 }
 if((Split-Path $Exe -Leaf) -eq 'mysqld.exe' -or $Exe -eq 'sc.exe' -or $Exe -eq 'icacls.exe'){return}
 throw 'Unexpected native program in synthetic recovery'
}
'@
Add-Content -LiteralPath (Join-Path $scripts 'runtime-common.ps1') -Value 'function Ensure-VisualCppRuntime {}'
Add-Content -LiteralPath (Join-Path $scripts 'sync-bootstrap-common.ps1') -Value @'
function Invoke-BootstrapSignedRequest {
 if($global:RecoveryCase -eq 'offline'){throw 'Synthetic internet outage'}
 [pscustomobject]@{protocolVersion=2;sourceId='11111111-1111-4111-8111-111111111111';schema='synthetic-schema';seq=11}
}
'@
foreach($folder in 'app','mysql\bin','keys','secrets','certs','storage'){New-Item -ItemType Directory -Force (Join-Path $source $folder)|Out-Null}
foreach($file in 'app\AlShifa.Api.exe','app\AlShifa.Api.dll','mysql\bin\mysqld.exe','mysql\bin\mysql.exe','certs\server.pfx','secrets\backup-password.txt'){
 [IO.File]::WriteAllText((Join-Path $source $file),'synthetic-never-executed')
}
$rsa=[Security.Cryptography.RSA]::Create();$rsa.set_KeySize(2048)
$request=[Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=AlShifa Synthetic Recovery Test',$rsa,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
$certificate=$request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1),[DateTimeOffset]::UtcNow.AddDays(1))
try{[IO.File]::WriteAllBytes((Join-Path $source 'certs\alshifa-ca.cer'),$certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))}finally{$certificate.Dispose();$rsa.Dispose()}
[IO.File]::WriteAllText((Join-Path $source 'keys\sync-installation-id'),'11111111-1111-4111-8111-111111111111')
[IO.File]::WriteAllText((Join-Path $source 'secrets\mysql-root.txt'),('R'*40))
[IO.File]::WriteAllText((Join-Path $source 'secrets\backup.cnf'),"user=alshifa_backup`npassword=$('B'*40)`nhost=127.0.0.1`nport=33494")
[IO.File]::WriteAllText((Join-Path $source 'secrets\restore.cnf'),"user=alshifa_restore`npassword=$('T'*40)`nhost=127.0.0.1`nport=33494")
$cfg=@{ConnectionStrings=@{Default="Server=127.0.0.1;Port=33494;Database=alshifa;User=alshifa;Password=$('A'*40)"};Mirror=@{Enabled=$false};Storage=@{Path='C:\AlShifa\storage'};DataProtection=@{KeysPath='C:\AlShifa\keys'};Backup=@{StatusFile='C:\AlShifa\logs\backup-status.json'};Sync=@{Url='https://shafa.novatechsoft.com';Key=('K'*40);RequireCompatibilityApproval=$false;IntervalMinutes=5};Kestrel=@{Endpoints=@{Loopback=@{Url='http://127.0.0.1:33495'};Lan=@{Url='https://0.0.0.0:33496';Certificate=@{Path='C:\AlShifa\certs\server.pfx';Password='synthetic'}}}}}
$cfg|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $source 'app\appsettings.Production.json') -Encoding UTF8
[IO.File]::WriteAllText((Join-Path $source 'app\release.json'),'{"version":"2026.11.20.1","schema":"synthetic-schema"}')
[IO.File]::WriteAllText((Join-Path $package 'database.sql.gpg'),'synthetic encrypted fixture')
[IO.File]::WriteAllText((Join-Path $package 'table-proof.txt'),"Patients|Id,Name|0`t0`t0`t0`t0")
$syntheticSql=Join-Path $work 'synthetic.sql';[IO.File]::WriteAllText($syntheticSql,'synthetic SQL never imported')
$files=@(foreach($path in 'app\AlShifa.Api.dll','app\appsettings.Production.json','mysql\bin\mysqld.exe','keys\sync-installation-id'){@{path=$path;sha256=(Get-FileHash -LiteralPath (Join-Path $source $path)).Hash}})
@{format=1;restoreVerified=$true;onlineHistoryVerified=$true;sourceFiles=$files;encryptedHash=(Get-FileHash -LiteralPath (Join-Path $package 'database.sql.gpg')).Hash;proofHash=(Get-FileHash -LiteralPath (Join-Path $package 'table-proof.txt')).Hash;sqlHash=(Get-FileHash -LiteralPath $syntheticSql).Hash}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $package 'recovery-manifest.json')
$before=@(Get-ChildItem -LiteralPath $source -File -Recurse|ForEach-Object {$_.FullName+'|'+(Get-FileHash -LiteralPath $_.FullName).Hash}|Sort-Object)
foreach($case in 'success','offline','failed-import','failed-health','failed-resume','failed-backup'){
 $global:RecoveryCase=$case;$global:RecoveryRestarts=0;$global:RecoveryStops=0;$global:RecoveryTasks=0;$global:RecoveryImports=0;$global:RecoveryUpdateTasks=0
 $global:RecoveryRoot='F:\AlShifa-RecoveryLab-'+[guid]::NewGuid().ToString('N')
 if(Test-Path -LiteralPath $global:RecoveryRoot){throw 'The synthetic root must be new'}
 $failed=$false
 try{& $entry -Source $source -Root $global:RecoveryRoot -Package $package -Apply|Out-Null}catch{$failed=$true}
 try{
  if($failed -ne ($case -in @('failed-import','failed-health','failed-backup'))){throw "Unexpected recovery outcome: $case"}
  if(Test-Path -LiteralPath (Join-Path $global:RecoveryRoot 'backup-staging\recovery.sql')){throw 'Plaintext recovery SQL was retained'}
  if(Test-Path -LiteralPath (Join-Path $global:RecoveryRoot 'backup-staging\root.cnf')){throw 'Temporary administrator credentials were retained'}
  if(-not $failed){
   $status=Get-Content (Join-Path $global:RecoveryRoot 'logs\replacement-recovery-status.json') -Raw|ConvertFrom-Json
   $actual=Get-Content (Join-Path $global:RecoveryRoot 'app\appsettings.Production.json') -Raw|ConvertFrom-Json
   if(-not $status.LocalHealthy -or $status.OriginalSyncSettingsResumed -ne ($case -eq 'success')){throw 'Recovery status is not truthful'}
   if(-not $status.UpdateDetectionConfigured -or $global:RecoveryUpdateTasks -ne 1){throw 'Update workers were not restored'}
   $updateSettings=Get-Content (Join-Path $global:RecoveryRoot 'secrets\auto-update.json') -Raw|ConvertFrom-Json
   if($updateSettings.autoInstall -ne $false){throw 'Recovery enabled unattended installation'}
   if($actual.Sync.RequireCompatibilityApproval -ne ($case -ne 'success')){throw 'Sync hold/resume differs from the verified outcome'}
   if($case -eq 'success' -and ($actual.Sync|ConvertTo-Json -Compress) -cne (($cfg.Sync|ConvertTo-Json|ConvertFrom-Json)|ConvertTo-Json -Compress)){throw 'Original Sync options were changed'}
   $ini=Get-Content (Join-Path $global:RecoveryRoot 'mysql\alshifa.ini') -Raw
   if($ini -notmatch 'innodb_buffer_pool_size=256M' -or $ini -notmatch 'performance_schema=OFF' -or $ini -match 'init-file='){throw 'Runtime memory/initialization configuration differs'}
   if($case -eq 'offline'){
    $global:RecoveryCase='success'
    $configPath=Join-Path $global:RecoveryRoot 'app\appsettings.Production.json'
    $edited=Get-Content $configPath -Raw|ConvertFrom-Json;$edited.Sync.IntervalMinutes=7
    $edited|ConvertTo-Json -Depth 12|Set-Content $configPath
    & (Join-Path $global:RecoveryRoot 'scripts\resume-recovered-sync.ps1') -Root $global:RecoveryRoot
    $afterEdit=Get-Content $configPath -Raw|ConvertFrom-Json
    if($afterEdit.Sync.IntervalMinutes -ne 7 -or -not $afterEdit.Sync.RequireCompatibilityApproval){throw 'Retry overwrote a changed Sync setting'}
    $edited.Sync.IntervalMinutes=5;$edited|ConvertTo-Json -Depth 12|Set-Content $configPath
    $identityPath=Join-Path $global:RecoveryRoot 'keys\sync-installation-id'
    [IO.File]::WriteAllText($identityPath,'22222222-2222-4222-8222-222222222222')
    & (Join-Path $global:RecoveryRoot 'scripts\resume-recovered-sync.ps1') -Root $global:RecoveryRoot
    if(-not(Get-Content $configPath -Raw|ConvertFrom-Json).Sync.RequireCompatibilityApproval){throw 'Retry resumed a changed installation identity'}
    Copy-Item -LiteralPath (Join-Path $source 'keys\sync-installation-id') -Destination $identityPath -Force
    & (Join-Path $global:RecoveryRoot 'scripts\resume-recovered-sync.ps1') -Root $global:RecoveryRoot
    $resumed=Get-Content (Join-Path $global:RecoveryRoot 'app\appsettings.Production.json') -Raw|ConvertFrom-Json
    if($resumed.Sync.RequireCompatibilityApproval -or (Test-Path (Join-Path $global:RecoveryRoot 'secrets\replacement-sync-original.json'))){throw 'Original Sync did not resume automatically after internet recovery'}
   }
  }
  "PASS: $case (synthetic host adapters only)."
 }finally{
  $resolved=[IO.Path]::GetFullPath($global:RecoveryRoot)
  if($resolved -notmatch '^F:\\AlShifa-RecoveryLab-[a-f0-9]{32}$'){throw 'Refused unexpected cleanup root'}
  Remove-Item -LiteralPath $resolved -Recurse -Force
 }
}
$after=@(Get-ChildItem -LiteralPath $source -File -Recurse|ForEach-Object {$_.FullName+'|'+(Get-FileHash -LiteralPath $_.FullName).Hash}|Sort-Object)
if(@(Compare-Object $before $after).Count){throw 'The source fixture changed'}
'PASS: current recovery entry source, offline hold, successful resume, failed import/health, failed resumed health and source preservation.'
