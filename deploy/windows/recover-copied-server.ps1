# Disaster recovery onto a replacement computer. Default is read-only preflight.
# No schema migration, enrollment, checkpoint reset, or old-command replay is performed.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Source,[string]$Root='C:\AlShifa-Recovered',[string]$Package=(Split-Path $PSScriptRoot -Parent),[string]$InstallerFolder,[switch]$Apply)
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'recovery-common.ps1')
. (Join-Path $PSScriptRoot 'runtime-common.ps1')
. (Join-Path $PSScriptRoot 'sync-bootstrap-common.ps1')
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$Source=[IO.Path]::GetFullPath($Source).TrimEnd('\')
$Root=[IO.Path]::GetFullPath($Root).TrimEnd('\')
$Package=[IO.Path]::GetFullPath($Package).TrimEnd('\')
if($Root -match '\s' -or $Root -notmatch '^[A-Za-z]:\\[^\\]+$' -or $Root -eq $Source -or $Source.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Choose a separate, empty top-level local folder without spaces.'}
if((Test-Path -LiteralPath $Root) -and ((Get-Item -LiteralPath $Root).Attributes -band [IO.FileAttributes]::ReparsePoint -or @(Get-ChildItem -LiteralPath $Root -Force).Count)){throw 'The destination is occupied. Nothing was replaced.'}
if(Get-Service AlShifaApi,AlShifaMySQL -ErrorAction SilentlyContinue){throw 'An installed AlShifa service already exists. Use the guarded upgrade or a reviewed recovery.'}
foreach($name in 'AlShifa Backup','AlShifa Update Check','AlShifa Update Install','AlShifa Sync Bootstrap','AlShifa Recovery Sync Resume'){
    if(Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue){throw 'An existing AlShifa task requires review before recovery.'}
}
$manifest=Get-Content -LiteralPath (Join-Path $Package 'recovery-manifest.json') -Raw|ConvertFrom-Json
if($manifest.format -ne 1 -or -not $manifest.restoreVerified -or -not $manifest.onlineHistoryVerified){throw 'This recovery package has not passed verification.'}
$cfg=Assert-RecoverySource $Source $manifest
foreach($pair in @(@{File='database.sql.gpg';Hash=$manifest.encryptedHash},@{File='table-proof.txt';Hash=$manifest.proofHash})){
    if((Get-FileHash -LiteralPath (Join-Path $Package $pair.File)).Hash -cne $pair.Hash){throw 'Recovery package integrity check failed.'}
}
$connection=New-Object System.Data.Common.DbConnectionStringBuilder
$connection.set_ConnectionString([string]$cfg.ConnectionStrings.Default)
$port=[int]$connection['Port'];$loopback=[uri]$cfg.Kestrel.Endpoints.Loopback.Url;$lan=[uri]$cfg.Kestrel.Endpoints.Lan.Url
if($loopback.Host -ne '127.0.0.1' -or $loopback.Scheme -ne 'http' -or $lan.Scheme -ne 'https' -or $port -lt 1024){throw 'Nonstandard endpoints require a reviewed recovery mapping.'}
foreach($number in @($port,$loopback.Port,$lan.Port)|Select-Object -Unique){if(-not(Test-PortFree $number)){throw 'A required local port is occupied. No installed application was stopped.'}}
if($cfg.DataProtection.KeysPath -ne 'C:\AlShifa\keys' -or $cfg.Storage.Path -ne 'C:\AlShifa\storage' -or $cfg.Sync.IdentityFile){throw 'Custom storage or identity paths require review.'}
if(Test-Path -LiteralPath (Join-Path $Source 'keys\update-maintenance')){throw 'The copied installation has an unfinished maintenance operation.'}
foreach($key in 'ConnectionStrings__Default','DataProtection__KeysPath','Storage__Path','Sync__Url','Sync__Key','Mirror__Enabled','ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT'){
    if([Environment]::GetEnvironmentVariable($key,'Process') -or [Environment]::GetEnvironmentVariable($key,'Machine')){throw 'An environment override requires review before recovery.'}
}
'Preflight passed: original copy and verified recovery data match; destination is empty.'
if(-not $Apply){return}
if(-not([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run recovery as Administrator.'}
$createdApi=$false;$createdMySql=$false;$createdTask=$false;$sql=$null;$init=$null;$complete=$false
function Wait-RecoveryHealth {
    foreach($attempt in 1..60){try{$reply=Invoke-WebRequest ($loopback.AbsoluteUri.TrimEnd('/')+'/health') -UseBasicParsing -TimeoutSec 3;if($reply.Content -eq 'Healthy'){return $true}}catch{};Start-Sleep -Seconds 2}
    return $false
}
try{
    '##STEP Copying the required application files'
    New-Item -ItemType Directory -Force $Root|Out-Null
    Protect-Path $Root
    Copy-RecoveryRuntime $Source $Root
    foreach($dir in 'data','logs','backup-staging','requests','scripts','archives'){New-Item -ItemType Directory -Force (Join-Path $Root $dir)|Out-Null}
    Copy-Item -Path (Join-Path $PSScriptRoot '*.ps1'),(Join-Path $PSScriptRoot '*.json') -Destination (Join-Path $Root 'scripts')
    $originalSync=$cfg.Sync|ConvertTo-Json -Depth 32 -Compress
    $cfg.Storage.Path=Join-Path $Root 'storage';$cfg.DataProtection.KeysPath=Join-Path $Root 'keys'
    $cfg.Kestrel.Endpoints.Lan.Certificate.Path=Join-Path $Root 'certs\server.pfx'
    $cfg.Backup.StatusFile=Join-Path $Root 'logs\backup-status.json'
    if(-not $cfg.Update){$cfg|Add-Member -NotePropertyName Update -NotePropertyValue ([pscustomobject]@{}) -Force}
    $cfg.Update|Add-Member -NotePropertyName StatusFile -NotePropertyValue (Join-Path $Root 'logs\update-status.json') -Force
    $cfg.Update|Add-Member -NotePropertyName ApprovalFile -NotePropertyValue (Join-Path $Root 'requests\update-approval.json') -Force
    $configFile=Join-Path $Root 'app\appsettings.Production.json'
    $cfg.Sync|Add-Member RequireCompatibilityApproval $true -Force
    [IO.File]::WriteAllText($configFile,($cfg|ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($false)))
    $mysqld=Join-Path $Root 'mysql\bin\mysqld.exe';$mysql=Join-Path $Root 'mysql\bin\mysql.exe'
    Ensure-VisualCppRuntime $mysqld (Join-Path $Root 'backup-staging\runtime') (Join-Path $Package 'prerequisites') $InstallerFolder
    $gpg=$null;try{$gpg=Find-Gpg}catch{}
    if(-not $gpg){
        $item=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerequisites.json') -Raw|ConvertFrom-Json).items|Where-Object id -eq 'gpg4win'
        $installer=Join-Path (Join-Path $Package 'prerequisites') $item.file
        if((Get-FileHash -LiteralPath $installer).Hash -cne $item.sha256){throw 'The approved backup encryption prerequisite is missing.'}
        $signature=Get-AuthenticodeSignature -LiteralPath $installer
        if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'g10 Code GmbH'){throw 'Backup encryption publisher verification failed.'}
        $installProcess=Start-Process -FilePath $installer -ArgumentList '/S' -WindowStyle Hidden -PassThru
        $null=$installProcess.Handle
        if(-not $installProcess.WaitForExit(600000) -or $installProcess.ExitCode -ne 0){throw 'Backup encryption installation did not finish.'}
    }
    '##STEP Opening the verified encrypted recovery data'
    $sql=Join-Path $Root 'backup-staging\recovery.sql'
    Unprotect-BackupFile (Find-Gpg) (Join-Path $Package 'database.sql.gpg') $sql (Join-Path $Root 'secrets\backup-password.txt') (Join-Path $Root 'backup-staging')
    if((Get-FileHash -LiteralPath $sql).Hash -cne $manifest.sqlHash){throw 'Decrypted recovery content differs from the verified data.'}
    $backup=@{};$restore=@{}
    foreach($line in Get-Content -LiteralPath (Join-Path $Root 'secrets\backup.cnf')){if($line -match '^(user|password)=(.*)$'){$backup[$Matches[1]]=$Matches[2]}}
    foreach($line in Get-Content -LiteralPath (Join-Path $Root 'secrets\restore.cnf')){if($line -match '^(user|password)=(.*)$'){$restore[$Matches[1]]=$Matches[2]}}
    if($backup.user -ne 'alshifa_backup' -or $restore.user -ne 'alshifa_restore'){throw 'Nonstandard recovery accounts require review.'}
    $rootPassword=([IO.File]::ReadAllText((Join-Path $Root 'secrets\mysql-root.txt'))).Trim()
    foreach($password in @($rootPassword,[string]$connection['Password'],$backup.password,$restore.password)){if($password -notmatch '^[A-Za-z0-9]{20,}$'){throw 'Nonstandard database credential encoding requires review.'}}
    $ini=Join-Path $Root 'mysql\alshifa.ini'
    $mysqlConfig=Get-MySqlIni (Join-Path $Root 'mysql') (Join-Path $Root 'data') (Join-Path $Root 'logs\mysql.err') $port
    $memory=(Get-CimInstance Win32_OperatingSystem).TotalVisibleMemorySize
    if($memory -lt 7.5MB){$mysqlConfig=$mysqlConfig.Replace('innodb_buffer_pool_size=512M','innodb_buffer_pool_size=256M')}
    $mysqlConfig|Set-Content -LiteralPath $ini -Encoding Ascii
    '##STEP Creating a clean database on this replacement computer'
    Invoke-Native $mysqld @("--defaults-file=$ini",'--initialize-insecure','--console')|Out-Null
    $init=Join-Path $Root 'backup-staging\initialize.sql'
    Get-MySqlBootstrapSql 'alshifa' $rootPassword ([string]$connection['Password']) $backup.password $restore.password|Set-Content -LiteralPath $init -Encoding Ascii
    Invoke-Native $mysqld @('--install','AlShifaMySQL',"--defaults-file=$ini")|Out-Null
    $createdMySql=$true
    Invoke-Native 'sc.exe' @('config','AlShifaMySQL','start=','auto','obj=','NT SERVICE\AlShifaMySQL')|Out-Null
    Protect-Path (Join-Path $Root 'mysql') @('NT SERVICE\AlShifaMySQL=RX')
    Protect-Path (Join-Path $Root 'data') @('NT SERVICE\AlShifaMySQL=F')
    Protect-Path (Join-Path $Root 'logs') @('NT SERVICE\AlShifaMySQL=M')
    Invoke-Native 'icacls.exe' @($Root,'/grant','NT SERVICE\AlShifaMySQL:(X)')|Out-Null
    Invoke-Native 'icacls.exe' @((Join-Path $Root 'backup-staging'),'/grant','NT SERVICE\AlShifaMySQL:(OI)(CI)R')|Out-Null
    Add-Content -LiteralPath $ini -Value "init-file=$($init -replace '\\','/')" -Encoding Ascii
    Start-Service AlShifaMySQL
    $cnf=Join-Path $Root 'backup-staging\root.cnf'
    Write-ClientCnf $cnf 'root' $rootPassword $port
    $ready=$false
    foreach($attempt in 1..60){try{Invoke-Native $mysql @("--defaults-extra-file=$cnf",'-e','SELECT 1')|Out-Null;$ready=$true;break}catch{Start-Sleep -Seconds 2}}
    if(-not $ready){throw 'The replacement database did not start.'}
    $facts=@(Invoke-Native $mysql @("--defaults-extra-file=$cnf",'-N','-B','-e','SELECT @@port,@@datadir;'))[-1] -split "`t"
    if([int]$facts[0] -ne $port -or [IO.Path]::GetFullPath($facts[1]).TrimEnd('\') -ne (Join-Path $Root 'data')){throw 'Replacement database identity mismatch.'}
    (Get-Content -LiteralPath $ini|Where-Object {$_ -notmatch '^init-file='})|Set-Content -LiteralPath $ini -Encoding Ascii
    Remove-Item -LiteralPath $init;$init=$null
    Invoke-Native $mysql @("--defaults-extra-file=$cnf",'--default-character-set=utf8mb4','alshifa','-e',"source $($sql -replace '\\','/')")|Out-Null
    '##STEP Verifying every restored table before starting the application'
    $proof=@(Get-RecoveryContentProof $mysql $cnf $port)
    if(@(Compare-Object @(Get-Content -LiteralPath (Join-Path $Package 'table-proof.txt')) $proof).Count){throw 'Restored table contents differ from the verified recovery data.'}
    $tableNames=@($proof|ForEach-Object {$_.Split('|')[0]})
    $checks='CHECK TABLE '+(@($tableNames|ForEach-Object {"``$_``"}) -join ',')+' EXTENDED;'
    $rows=@(Invoke-Native $mysql @("--defaults-extra-file=$cnf",'-N','-B','alshifa','-e',$checks))
    $checked=@($rows|Where-Object {$_ -match '\tstatus\tOK$'}|ForEach-Object {(($_ -split "`t")[0] -split '\.')[-1]}|Select-Object -Unique)
    if(@(Compare-Object $tableNames $checked).Count -or @($rows|Where-Object {$_ -match '\terror\t'}).Count){throw 'A recovered table failed its integrity check.'}
    Remove-Item -LiteralPath $sql;$sql=$null
    Remove-Item -LiteralPath $cnf
    '##STEP Registering the restored hospital application'
    $api=Join-Path $Root 'app\AlShifa.Api.exe'
    Invoke-Native 'sc.exe' @('create','AlShifaApi','binPath=',"`"$api`"",'start=','auto','depend=','AlShifaMySQL','obj=','NT SERVICE\AlShifaApi','DisplayName=','Al-Shifa Hospital Application')|Out-Null
    $createdApi=$true
    foreach($service in 'AlShifaApi','AlShifaMySQL'){Invoke-Native 'sc.exe' @('failure',$service,'reset=','86400','actions=','restart/5000/restart/15000/restart/60000')|Out-Null;Invoke-Native 'sc.exe' @('failureflag',$service,'1')|Out-Null}
    New-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\AlShifaApi' -Name Environment -PropertyType MultiString -Value @('ASPNETCORE_ENVIRONMENT=Production') -Force|Out-Null
    Protect-Path (Join-Path $Root 'app') @('NT SERVICE\AlShifaApi=RX')
    Protect-Path (Join-Path $Root 'certs') @('NT SERVICE\AlShifaApi=R')
    Protect-Path (Join-Path $Root 'storage') @('NT SERVICE\AlShifaApi=M')
    Protect-Path (Join-Path $Root 'keys') @('NT SERVICE\AlShifaApi=M')
    Invoke-Native 'icacls.exe' @($Root,'/grant','NT SERVICE\AlShifaApi:(X)')|Out-Null
    Invoke-Native 'icacls.exe' @((Join-Path $Root 'logs'),'/grant','NT SERVICE\AlShifaApi:(OI)(CI)R')|Out-Null
    if(-not[Diagnostics.EventLog]::SourceExists('AlShifa.Api')){[Diagnostics.EventLog]::CreateEventSource('AlShifa.Api','Application')}
    Start-Service AlShifaApi
    $healthy=Wait-RecoveryHealth
    if(-not $healthy){throw 'The restored application did not pass its local health check.'}
    $backupArgs="-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Root 'scripts\backup.ps1')`" -Root `"$Root`" -Destination `"$(Join-Path $Root 'archives')`" -AllowSameDisk"
    $settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName 'AlShifa Backup' -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $backupArgs) -Trigger (New-KabulBackupTrigger) -Settings $settings -User SYSTEM -RunLevel Highest|Out-Null
    $createdTask=$true;Add-BackupRequestTrigger
    '##STEP Taking and restoring the first encrypted backup'
    & (Join-Path $Root 'scripts\backup.ps1') -Root $Root -Destination (Join-Path $Root 'archives') -AllowSameDisk|Out-Null
    if($LASTEXITCODE){throw 'The first recovery backup failed.'}
    $backupStatus=Get-Content -LiteralPath (Join-Path $Root 'logs\backup-status.json') -Raw|ConvertFrom-Json
    if(-not $backupStatus.restoreVerifiedAt -or $backupStatus.lastError){throw 'The first recovery backup did not pass restore verification.'}
    New-NetFirewallRule -DisplayName 'Al-Shifa HTTPS (hospital LAN)' -Direction Inbound -Protocol TCP -LocalPort $lan.Port -RemoteAddress LocalSubnet -Profile Any -Action Allow|Out-Null
    $ca=Join-Path $Root 'certs\alshifa-ca.cer'
    Import-Certificate -FilePath $ca -CertStoreLocation Cert:\LocalMachine\Root|Out-Null
    $thumbprint=(New-Object Security.Cryptography.X509Certificates.X509Certificate2($ca)).Thumbprint
    Install-AlShifaLauncher 'سیستم الشفا' (Join-Path $Package 'tools\AlShifa.exe') (Join-Path $Root 'app\wwwroot\favicon.ico') $lan.Port $thumbprint
    # Scheduled tasks do not travel with a copied installation.
    $updatesReady=$false
    try {
        . (Join-Path $PSScriptRoot 'auto-update-common.ps1')
        Enable-PublisherFeed $Root $PSScriptRoot (Join-Path $Root 'archives') $true | Out-Null
        Set-AutoUpdateTasks $Root
        $updatesReady=(Test-Path -LiteralPath (Join-Path $Root 'secrets\auto-update.json')) -and (Test-Path -LiteralPath (Join-Path $Root 'app\release.json'))
    } catch { 'Update detection needs technical review; recovered data and Sync settings are retained.' }
    # The exact original settings resume only after a read-only history check.
    $syncReady=$false;$resumeAttempted=$false
    try{
        $remote=Invoke-BootstrapSignedRequest $cfg.Sync.Url $cfg.Sync.Key '/api/sync/mirror/state'
        $state=@(Invoke-Native $mysql @("--defaults-extra-file=$(Join-Path $Root 'secrets\backup.cnf')",'-N','-B','alshifa','-e',"SELECT (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1),COALESCE((SELECT Value FROM SyncStates WHERE ``Key``='push.ackedSeq'),'0'),COALESCE((SELECT Value FROM SyncStates WHERE ``Key``='push.seq'),'0');"))[-1] -split "`t"
        $id=([IO.File]::ReadAllText((Join-Path $Root 'keys\sync-installation-id'))).Trim()
        $decision=Get-SyncBootstrapDecision @{KeyPresent=$true;ApprovedUrl=$true;IdentityPresent=$true;Identity=$id;Schema=$state[0];Acked=[long]$state[1];Sent=[long]$state[2]} $remote
        if($decision.Ready){
            $resumeAttempted=$true
            $cfg.Sync=$originalSync|ConvertFrom-Json
            [IO.File]::WriteAllText($configFile,($cfg|ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($false)))
            Restart-Service AlShifaApi
            if(-not(Wait-RecoveryHealth)){throw 'Resumed application health failed.'}
            $syncReady=$true
        }
    }catch{
        if($resumeAttempted){
            $cfg.Sync|Add-Member RequireCompatibilityApproval $true -Force
            [IO.File]::WriteAllText($configFile,($cfg|ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($false)))
            Restart-Service AlShifaApi
            if(-not(Wait-RecoveryHealth)){throw 'The restored local application could not resume safely.'}
        }
    }
    if(-not $syncReady){
        $snapshot=@{Sync=($originalSync|ConvertFrom-Json);IdentityHash=(Get-FileHash -LiteralPath (Join-Path $Root 'keys\sync-installation-id')).Hash}
        [IO.File]::WriteAllText((Join-Path $Root 'secrets\replacement-sync-original.json'),($snapshot|ConvertTo-Json -Depth 32))
        Set-RecoverySyncRetryTask $Root
    }
    [IO.File]::WriteAllText((Join-Path $Root 'logs\replacement-recovery-status.json'),(@{LocalHealthy=$true;TablesVerified=$proof.Count;OriginalIdentityPreserved=$true;OriginalSyncSettingsResumed=$syncReady;RecoverySyncRetryScheduled=(-not $syncReady);UpdateDetectionConfigured=$updatesReady;PhysicalHospitalAcceptance=$false;BackupOnSameDisk=$true;InitialBackupRestoreVerified=$true}|ConvertTo-Json))
    $complete=$true
    'Recovery complete. Local operation verified. Keep the original folder and the recovery package.'
    if($syncReady){'Original Sync settings resumed after compatibility verification.'}else{'Local operation is available. Original Sync will retry compatibility automatically when internet returns.'}
    'The backup destination is currently on this laptop. Move backups to a separate disk before hospital acceptance.'
}catch{
    if($createdApi){Stop-Service AlShifaApi -ErrorAction SilentlyContinue}
    if($createdMySql){Stop-Service AlShifaMySQL -ErrorAction SilentlyContinue}
    # Keep the destination and its logs. Never erase the original source or recover over a previous installation.
    throw 'Recovery stopped safely. The original copied folder was not replaced. Keep the protected destination for technical review.'
}finally{
    foreach($file in @($sql,$init,(Join-Path $Root 'backup-staging\root.cnf'))){if($file -and (Test-Path -LiteralPath $file)){Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue}}
}
