# Read-only content proofs for an isolated restored database. No row values are returned.
function Assert-UpgradeEnvironment($Config,[string]$Root) {
    $expected=@{ConnectionStrings__Default=[string]$Config.ConnectionStrings.Default;DataProtection__KeysPath=[string]$Config.DataProtection.KeysPath;Storage__Path=[string]$Config.Storage.Path;ASPNETCORE_ENVIRONMENT='Production';DOTNET_ENVIRONMENT='Production';Mirror__Enabled='false';Sync__RequireCompatibilityApproval='true'}
    $expected.Sync__Url=[string]$Config.Sync.Url
    $expected.Sync__Key=[string]$Config.Sync.Key
    $expected.Sync__IdentityFile=if($Config.Sync.IdentityFile){[string]$Config.Sync.IdentityFile}else{Join-Path ([string]$Config.DataProtection.KeysPath) 'sync-installation-id'}
    $serviceValues=@{}
    $service=(Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\AlShifaApi' -Name Environment -ErrorAction SilentlyContinue).Environment
    foreach($entry in $service){if($entry -match '^([^=]+)=(.*)$'){$serviceValues[$Matches[1]]=$Matches[2]}}
    foreach($key in $expected.Keys){
        foreach($value in @([Environment]::GetEnvironmentVariable($key,'Process'),[Environment]::GetEnvironmentVariable($key,'Machine'),$serviceValues[$key])){
            if($value -and $value -cne $expected[$key]){throw 'An environment override conflicts with the verified installation. Nothing was stopped or migrated.'}
        }
    }
    if([IO.Path]::GetFullPath($Config.Storage.Path).TrimEnd('\') -ne (Join-Path $Root 'storage') -or [IO.Path]::GetFullPath($Config.DataProtection.KeysPath).TrimEnd('\') -ne (Join-Path $Root 'keys')){throw 'Custom storage/key locations require a reviewed backup mapping before upgrade.'}
}

function Get-UpgradeDataProof([string]$MySql,[string]$Cnf,[string]$Database,$Layout=$null,[switch]$ReadOnlySource) {
    if($Database -notmatch '^alshifa_restore_[a-z0-9_]+$' -and -not($ReadOnlySource -and $Database -eq 'alshifa')){throw 'Upgrade rehearsal requires a scratch restore database.'}
    $tables=@('Patients','Products','StockBatches','StockMovements','StockAdjustments','Invoices','InvoiceItems','Payments','Refunds','LedgerEntries','AspNetUsers','AspNetUserRoles','AspNetUserTokens','SyncStates','SyncChanges','SyncCommands','QueueCards','QueueAttendances','QueueDeskLanes')
    if($null -eq $Layout){
        $Layout=@()
        foreach($table in $tables){
            $columns=@(Invoke-Native $MySql @("--defaults-extra-file=$Cnf",'-N','-B',$Database,'-e',"SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='$table' ORDER BY ORDINAL_POSITION"))
            if(-not $columns.Count){continue}
            foreach($column in $columns){if($column -notmatch '^[A-Za-z0-9_]+$'){throw 'Unexpected column identifier.'}}
            $Layout+=@{table=$table;columns=$columns}
        }
        foreach($required in 'Patients','Products','StockBatches','StockMovements','Payments','AspNetUsers'){
            if($Layout.table -notcontains $required){throw 'A required preservation table is missing.'}
        }
    }
    $proof=@()
    foreach($item in $Layout){
        if($tables -notcontains $item.table){throw 'Unexpected preservation table.'}
        $parts=@($item.columns | ForEach-Object {if($_ -notmatch '^[A-Za-z0-9_]+$'){throw 'Unexpected column identifier.'}; "IFNULL(CONCAT('V',HEX(CAST(``$_`` AS BINARY))),'N')"})
        $hash="SHA2(CONCAT_WS('|',$($parts -join ',')),256)"
        $sums=@(1,17,33,49 | ForEach-Object {"COALESCE(SUM(CAST(CONV(SUBSTRING(h,$_,16),16,10) AS DECIMAL(65,0))),0)"})
        $sql="SELECT COUNT(*),$($sums -join ',') FROM (SELECT $hash AS h FROM ``$($item.table)``) p"
        $value=@(Invoke-Native $MySql @("--defaults-extra-file=$Cnf",'-N','-B',$Database,'-e',$sql))
        if($value.Count -ne 1 -or $value[0] -notmatch '^\d+(\t\d+){4}$'){throw 'Preservation proof could not be computed.'}
        $proof+="$($item.table)|$($value[0])"
    }
    return @{layout=$Layout;values=$proof}
}

function Test-UpgradeCandidate([string]$MySql,[string]$Cnf,[string]$Database,[string]$CandidateApp,[string]$Work) {
    $before=Get-UpgradeDataProof $MySql $Cnf $Database
    $client=@{}
    foreach($line in (Get-Content -LiteralPath $Cnf)){if($line -match '^(host|port|user|password)=(.*)$'){$client[$Matches[1]]=$Matches[2]}}
    if($client.host -notin @('127.0.0.1','localhost') -or -not $client.port -or $client.user -ne 'alshifa_restore'){throw 'Only the restricted local restore account can rehearse an upgrade.'}
    $connection=New-Object System.Data.Common.DbConnectionStringBuilder
    foreach($key in 'host','port','user','password'){$connection[$(switch($key){host{'Server'};user{'User'};default{$key}})]=$client[$key]}
    $connection['Database']=$Database
    $values=@{ConnectionStrings__Default=$connection.get_ConnectionString();ASPNETCORE_ENVIRONMENT='Production';Mirror__Enabled='false';Sync__Url='';Storage__Path=(Join-Path $Work 'candidate-storage');DataProtection__KeysPath=(Join-Path $Work 'candidate-keys')}
    $old=@{}
    try {
        foreach($key in $values.Keys){$old[$key]=[Environment]::GetEnvironmentVariable($key,'Process');[Environment]::SetEnvironmentVariable($key,$values[$key],'Process')}
        $clock=[Diagnostics.Stopwatch]::StartNew()
        Invoke-Native (Join-Path $CandidateApp 'AlShifa.Api.exe') @('--migrate','--backup-taken','--contentRoot',$Work) | Out-Null
        $migrationSeconds=[int][Math]::Ceiling($clock.Elapsed.TotalSeconds)
        $after=Get-UpgradeDataProof $MySql $Cnf $Database $before.layout
        if(@(Compare-Object $before.values $after.values).Count){throw 'The candidate changed protected business rows or account credentials.'}
    } catch {
        # Do not expose native output or credentials in installer logs.
        throw 'Upgrade compatibility/preservation rehearsal failed on the restored copy. The installed database was not migrated.'
    } finally {
        foreach($key in $old.Keys){[Environment]::SetEnvironmentVariable($key,$old[$key],'Process')}
    }
    'RESULT: candidate migrations and protected data preservation verified on restored copy'
    "MIGRATION-SECONDS: $migrationSeconds"
}
