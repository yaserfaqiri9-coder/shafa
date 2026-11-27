# Read-only compatibility retry for a replacement server installed without internet.
# Never enrolls a source, enables a paused worker, writes SQL, or replays commands.
param([string]$Root='C:\AlShifa')
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'sync-bootstrap-common.ps1')
$Root=[IO.Path]::GetFullPath($Root).TrimEnd('\')
$snapshotFile=Join-Path $Root 'secrets\replacement-sync-original.json'
if(-not(Test-Path -LiteralPath $snapshotFile)){return}
$status=@{OriginalSyncSettingsResumed=$false;Reason='waiting_for_internet_or_compatibility';RealHospitalSyncAccepted=$false}
$attempted=$false;$configFile=Join-Path $Root 'app\appsettings.Production.json'
function Recovery-Healthy {
    foreach($attempt in 1..30){try{if((Invoke-WebRequest ($cfg.Kestrel.Endpoints.Loopback.Url.TrimEnd('/')+'/health') -UseBasicParsing -TimeoutSec 3).Content -eq 'Healthy'){return $true}}catch{};Start-Sleep -Seconds 2}
    return $false
}
try{
    $api=Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
    $mysqlService=Get-CimInstance Win32_Service -Filter "Name='AlShifaMySQL'"
    if(-not $api -or $api.PathName.Trim().Trim('"') -ne (Join-Path $Root 'app\AlShifa.Api.exe') -or -not $mysqlService -or $mysqlService.PathName -notmatch ('^"?'+[regex]::Escape((Join-Path $Root 'mysql\bin\mysqld.exe'))+'"?(\s|$)')){throw 'Recovery service mapping differs.'}
    $cfg=Get-Content -LiteralPath $configFile -Raw|ConvertFrom-Json
    $snapshot=Get-Content -LiteralPath $snapshotFile -Raw|ConvertFrom-Json
    if((Get-FileHash -LiteralPath (Join-Path $Root 'keys\sync-installation-id')).Hash -cne $snapshot.IdentityHash){$status.Reason='identity_changed';throw 'Identity changed.'}
    $held=$snapshot.Sync|ConvertTo-Json -Depth 32|ConvertFrom-Json
    $held|Add-Member RequireCompatibilityApproval $true -Force
    if(($cfg.Sync|ConvertTo-Json -Depth 32 -Compress) -cne ($held|ConvertTo-Json -Depth 32 -Compress)){$status.Reason='configuration_changed';throw 'Configuration changed.'}
    $db=New-Object System.Data.Common.DbConnectionStringBuilder;$db.set_ConnectionString([string]$cfg.ConnectionStrings.Default)
    if($db['Server'] -notin @('127.0.0.1','localhost') -or $db['Database'] -ne 'alshifa' -or [int]$db['Port'] -eq 3308){throw 'Unverified recovery endpoint.'}
    $cnf=Join-Path $Root 'secrets\backup.cnf';$mysql=Join-Path $Root 'mysql\bin\mysql.exe'
    $endpoint=@(Invoke-Native $mysql @("--defaults-extra-file=$cnf",'--protocol=TCP','--host=127.0.0.1',"--port=$($db['Port'])",'-N','-B','-e','SELECT @@port,@@datadir;'))[-1] -split "`t"
    if([int]$endpoint[0] -ne [int]$db['Port'] -or [IO.Path]::GetFullPath($endpoint[1]).TrimEnd('\') -ne (Join-Path $Root 'data')){throw 'Database identity differs.'}
    $state=@(Invoke-Native $mysql @("--defaults-extra-file=$cnf",'--protocol=TCP','--host=127.0.0.1',"--port=$($db['Port'])",'-N','-B','alshifa','-e',"SELECT (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1),COALESCE((SELECT Value FROM SyncStates WHERE ``Key``='push.ackedSeq'),'0'),COALESCE((SELECT Value FROM SyncStates WHERE ``Key``='push.seq'),'0');"))[-1] -split "`t"
    $remote=Invoke-BootstrapSignedRequest $snapshot.Sync.Url $snapshot.Sync.Key '/api/sync/mirror/state'
    $identity=([IO.File]::ReadAllText((Join-Path $Root 'keys\sync-installation-id'))).Trim()
    $decision=Get-SyncBootstrapDecision @{KeyPresent=$true;ApprovedUrl=$true;IdentityPresent=$true;Identity=$identity;Schema=$state[0];Acked=[long]$state[1];Sent=[long]$state[2]} $remote
    if(-not $decision.Ready){$status.Reason=$decision.Reason;throw 'Compatibility still requires review.'}
    $attempted=$true
    $cfg.Sync=$snapshot.Sync
    [IO.File]::WriteAllText($configFile,($cfg|ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($false)))
    Restart-Service AlShifaApi
    if(-not(Recovery-Healthy)){throw 'Resumed application health failed.'}
    $status.OriginalSyncSettingsResumed=$true;$status.Reason='original_configuration_resumed'
    Remove-Item -LiteralPath $snapshotFile
    Unregister-ScheduledTask -TaskName 'AlShifa Recovery Sync Resume' -Confirm:$false -ErrorAction SilentlyContinue
}catch{
    if($attempted){
        $cfg.Sync=$held
        [IO.File]::WriteAllText($configFile,($cfg|ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($false)))
        try{Restart-Service AlShifaApi|Out-Null}catch{}
        $status.Reason='resume_health_failed'
    }
}finally{
    [IO.File]::WriteAllText((Join-Path $Root 'logs\replacement-sync-resume-status.json'),($status|ConvertTo-Json))
}
