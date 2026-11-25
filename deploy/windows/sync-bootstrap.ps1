# Upgrade-only automatic checks. Never enrolls an occupied mirror or alters history/outbox.
#Requires -RunAsAdministrator
param([string]$Root='C:\AlShifa',[switch]$NoRestart)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'sync-bootstrap-common.ps1')
. (Join-Path $PSScriptRoot 'sync-upgrade-common.ps1')
$Root=[IO.Path]::GetFullPath($Root).TrimEnd('\')
$status=[ordered]@{Ready=$false;Reason='installation_verification';RealSyncAccepted=$false;CheckedAt=[DateTimeOffset]::UtcNow.ToString('o')}
$cnf=$null;$before=$null;$configChanged=$false;$configFile=Join-Path $Root 'app\appsettings.Production.json'
function Write-Config($Value) {
    $temporary=$configFile+'.bootstrap-'+[guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($temporary,($Value|ConvertTo-Json -Depth 32),(New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $configFile -Force
}
try {
    $service=Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
    $databaseService=Get-CimInstance Win32_Service -Filter "Name='AlShifaMySQL'"
    if(-not $service -or $service.PathName.Trim().Trim('"') -ne (Join-Path $Root 'app\AlShifa.Api.exe') -or -not $databaseService -or $databaseService.PathName -notmatch ('^"?'+[regex]::Escape((Join-Path $Root 'mysql\bin\mysqld.exe'))+'"?(\s|$)')){throw 'Installation mismatch.'}
    $before=Get-Content -LiteralPath $configFile -Raw
    $config=$before|ConvertFrom-Json
    if($config.Mirror.Enabled){throw 'Online host is not a local source.'}
    foreach($key in 'Sync__Url','Sync__Key','Sync__IdentityFile','Sync__RequireCompatibilityApproval','DataProtection__KeysPath','ConnectionStrings__Default'){
        $serviceEnv=(Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\AlShifaApi' -Name Environment -ErrorAction SilentlyContinue).Environment
        if([Environment]::GetEnvironmentVariable($key,'Machine') -or [Environment]::GetEnvironmentVariable($key,'Process') -or @($serviceEnv|Where-Object {$_ -like "$key=*"}).Count){throw 'Environment overrides require review.'}
    }
    # A leftover preparation task must not put an enrolled installation back on
    # hold merely because the internet is down. The normal worker owns retries.
    $existingSync=Get-EstablishedSyncUpgradeSnapshot $config $Root
    if($existingSync){
        $status.Ready=$true;$status.Reason='existing_configuration_preserved'
        $status['ExistingConfigurationPreserved']=$true
        return
    }
    $db=New-Object System.Data.Common.DbConnectionStringBuilder
    $db.set_ConnectionString([string]$config.ConnectionStrings.Default)
    if($db['Server'] -notin @('127.0.0.1','localhost') -or $db['Database'] -ne 'alshifa'){throw 'Unexpected local database.'}
    $secrets=Join-Path $Root 'secrets';Protect-Path $secrets
    $cnf=Join-Path $secrets ('bootstrap-'+[guid]::NewGuid().ToString('N')+'.cnf')
    function CnfValue([string]$Value){if($Value.Contains([char]0)){throw 'Invalid credential format.'};'"'+$Value.Replace('\','\\').Replace('"','\"').Replace("`r",'\r').Replace("`n",'\n')+'"'}
    $user=if($db.ContainsKey('User')){[string]$db['User']}else{[string]$db['User ID']}
    $client="[client]`nuser=$(CnfValue $user)`npassword=$(CnfValue ([string]$db['Password']))`nhost=127.0.0.1`nport=$([int]$db['Port'])`nprotocol=TCP`ndefault-character-set=utf8mb4`n"
    [IO.File]::WriteAllText($cnf,$client,(New-Object Text.UTF8Encoding($false)))
    $mysql=Join-Path $Root 'mysql\bin\mysql.exe'
    $identity=@(Invoke-Native $mysql @("--defaults-extra-file=$cnf",'-N','-B','-e','SELECT @@port,@@datadir;'))[-1] -split "`t"
    if([int]$identity[0] -ne [int]$db['Port'] -or [IO.Path]::GetFullPath($identity[1]).TrimEnd('\') -ne (Join-Path $Root 'data')){throw 'Database identity mismatch.'}
    if(-not $config.PSObject.Properties['Sync']){$config|Add-Member Sync ([pscustomobject]@{})}
    $config.Sync|Add-Member RequireCompatibilityApproval $true -Force
    if(-not $config.Sync.Url){$config.Sync|Add-Member Url 'https://shafa.novatechsoft.com' -Force}
    if(-not $config.Sync.Key -and (Test-Path -LiteralPath (Join-Path $secrets 'sync-key.txt'))){$config.Sync|Add-Member Key ((Get-Content -LiteralPath (Join-Path $secrets 'sync-key.txt') -Raw).Trim()) -Force}
    if(-not $config.Sync.IntervalMinutes){$config.Sync|Add-Member IntervalMinutes 5 -Force}
    $uri=[uri]$config.Sync.Url
    $approved=$uri.Scheme -eq 'https' -and $uri.DnsSafeHost -eq 'shafa.novatechsoft.com' -and $uri.Port -eq 443 -and -not $uri.UserInfo -and -not $uri.Query -and -not $uri.Fragment -and $uri.AbsolutePath -eq '/'
    $identityFile=if($config.Sync.IdentityFile){[string]$config.Sync.IdentityFile}else{Join-Path ([string]$config.DataProtection.KeysPath) 'sync-installation-id'}
    if([IO.Path]::GetFullPath($identityFile) -ne (Join-Path $Root 'keys\sync-installation-id')){throw 'Custom identity mapping requires review.'}
    if(-not (Test-Path -LiteralPath $identityFile)){[IO.File]::WriteAllText($identityFile,[guid]::NewGuid().ToString('D'))}
    $source=(Get-Content -LiteralPath $identityFile -Raw).Trim();$parsed=[guid]::Empty
    $validIdentity=[guid]::TryParse($source,[ref]$parsed) -and $parsed -ne [guid]::Empty
    $q="SELECT (SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1),COALESCE((SELECT Value FROM SyncStates WHERE ``Key``='push.ackedSeq'),'0'),COALESCE((SELECT Value FROM SyncStates WHERE ``Key``='push.seq'),'0');"
    $values=@(Invoke-Native $mysql @("--defaults-extra-file=$cnf",'-N','-B','alshifa','-e',$q))[-1] -split "`t"
    $local=@{KeyPresent=([string]$config.Sync.Key).Length -ge 32;ApprovedUrl=$approved;IdentityPresent=$validIdentity;Identity=$source;Schema=$values[0];Acked=[long]$values[1];Sent=[long]$values[2]}
    $decision=Get-SyncBootstrapDecision $local ([pscustomobject]@{})
    if($local.KeyPresent -and $approved -and $validIdentity){
        $status.Reason='online_unreachable_or_authentication_failed'
        $remote=Invoke-BootstrapSignedRequest $config.Sync.Url $config.Sync.Key '/api/sync/mirror/state'
        $decision=Get-SyncBootstrapDecision $local $remote
        if($decision.Ready){
            $commands=@(Invoke-BootstrapSignedRequest $config.Sync.Url $config.Sync.Key '/api/sync/mirror/commands/pending' 'POST')
            $old=@($commands|Where-Object {$_.schema -and $_.schema -ne $local.Schema}).Count
            $decision=Get-SyncBootstrapDecision $local $remote $old
        }
    }
    $status.Ready=$decision.Ready;$status.Reason=$decision.Reason
    $commitReason=$status.Reason
    $status.Reason='configuration_commit_failed'
    if($decision.Ready){
        $config.Sync.RequireCompatibilityApproval=$false
        Invoke-Native $mysql @("--defaults-extra-file=$cnf",'alshifa','-e',"INSERT INTO SyncStates (``Key``,Value,UpdatedAt) VALUES ('push.enabled','true',UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE Value='true',UpdatedAt=UTC_TIMESTAMP(6);")|Out-Null
    }
    if(($config|ConvertTo-Json -Depth 32) -cne $before){
        $backup=Join-Path $secrets ('config-before-sync-bootstrap-'+[guid]::NewGuid().ToString('N')+'.json')
        [IO.File]::WriteAllText($backup,$before)
        Write-Config $config;$configChanged=$true
    }
    if($decision.Ready -and -not $NoRestart -and $configChanged){Restart-Service AlShifaApi -ErrorAction Stop}
    $status.Reason=$commitReason
} catch {
    $status.Ready=$false
    if($configChanged -and $before){[IO.File]::WriteAllText($configFile,$before);try{Restart-Service AlShifaApi -ErrorAction Stop}catch{}}
    # Never serialize exception text: responses may contain secrets or patient payloads.
} finally {
    if($cnf -and (Test-Path -LiteralPath $cnf)){Remove-Item -LiteralPath $cnf -Force}
    $path=Join-Path $Root 'logs\sync-bootstrap-status.json'
    if(Test-Path -LiteralPath (Split-Path $path)){[IO.File]::WriteAllText($path,($status|ConvertTo-Json))}
    if($status.Ready -and -not $NoRestart){Set-SyncBootstrapTask $Root}
    if($status.Ready){'Sync configuration verified; real hospital acceptance remains pending.'}else{'Sync held safely; local operation is preserved. See the protected bootstrap status.'}
}
