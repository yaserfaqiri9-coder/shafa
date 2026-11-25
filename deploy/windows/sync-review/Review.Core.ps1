$ErrorActionPreference='Stop'
function Protect-ReviewDirectory([string]$Path) {
    if(-not(Test-Path -LiteralPath $Path)){[IO.Directory]::CreateDirectory($Path)|Out-Null}
    if((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'review_reparse_path'}
    $acl=New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true,$false)
    foreach($sid in @('S-1-5-18','S-1-5-32-544',[Security.Principal.WindowsIdentity]::GetCurrent().User.Value)|Select-Object -Unique){
        $identity=New-Object Security.Principal.SecurityIdentifier($sid)
        $rule=New-Object Security.AccessControl.FileSystemAccessRule($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
        $acl.AddAccessRule($rule)
    }
    ([IO.DirectoryInfo]$Path).SetAccessControl($acl)
}
function Assert-ReviewPath([string]$Path,[string]$Parent) {
    $full=[IO.Path]::GetFullPath($Path)
    $base=[IO.Path]::GetFullPath($Parent).TrimEnd('\')+'\'
    if(-not $full.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)){throw 'review_path_escape'}
    $node=Get-Item -LiteralPath $full -Force -ErrorAction SilentlyContinue
    while($node){
        if($node.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'review_reparse_path'}
        $node=$node.Parent
    }
    return $full
}
function New-ReviewClientFile([string]$Path,[string]$User,[string]$Password,[int]$Port) {
    function Cnf([string]$Value){if($Value.Contains([char]0)){throw 'credential_format'};'"'+$Value.Replace('\','\\').Replace('"','\"').Replace("`r",'\r').Replace("`n",'\n')+'"'}
    $text="[client]`nuser=$(Cnf $User)`npassword=$(Cnf $Password)`nhost=127.0.0.1`nport=$Port`nprotocol=TCP`ndefault-character-set=utf8mb4`n"
    [IO.File]::WriteAllText($Path,$text,(New-Object Text.UTF8Encoding($false)))
}
function Invoke-ReviewNative([string]$Exe,[string[]]$Arguments,[string]$Private,[string]$InputFile=$null,[int]$TimeoutMs=600000) {
    $errorFile=Join-Path $Private ('native-'+[guid]::NewGuid().ToString('N')+'.err')
    $result=[AlShifa.SyncReview.Native]::Run($Exe,$Arguments,$InputFile,$errorFile,$TimeoutMs)
    if($result.ExitCode -ne 0){throw 'native_operation_failed'}
    return $result.Output
}
function Read-ReviewSql([string]$Mysql,[string]$Client,[string]$Sql,[string]$Private) {
    if(-not $Sql.StartsWith('SELECT ') -or $Sql.TrimEnd(';').Contains(';')){throw 'source_query_not_readonly'}
    $text=Invoke-ReviewNative $Mysql @("--defaults-file=$Client",'-N','-B','--raw','--binary-as-hex','--init-command=SET time_zone=''+00:00''','alshifa','-e',$Sql) $Private
    if(-not $text){return @()}
    return @($text.TrimEnd("`r","`n") -split '\r?\n')
}
function Quote-ReviewName([string]$Name) {
    if($Name -notmatch '^[A-Za-z0-9_]+$'){throw 'unexpected_schema_identifier'}
    return ('`'+$Name+'`')
}
function Get-ReviewExpression([string[]]$Names,[switch]$JsonKey) {
    $parts=@()
    for($i=0;$i -lt $Names.Count;$i++){
        $value=if($JsonKey){"JSON_UNQUOTE(JSON_EXTRACT(KeyJson,'`$[$i]'))"}else{Quote-ReviewName $Names[$i]}
        $parts+="IFNULL(CONCAT('V',HEX(CAST($value AS BINARY))),'N')"
    }
    if(-not $parts.Count){throw 'empty_schema_columns'}
    return "SHA2(CONCAT_WS('|',$($parts -join ',')),256)"
}
function Get-ReviewLayout([string]$Mysql,[string]$Client,[string]$Private) {
    $layout=[ordered]@{}
    $tables=@(Read-ReviewSql $Mysql $Client "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA='alshifa' AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME" $Private)
    foreach($table in $tables){
        [void](Quote-ReviewName $table)
        $columns=@(Read-ReviewSql $Mysql $Client "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='alshifa' AND TABLE_NAME='$table' ORDER BY COLUMN_NAME" $Private)
        $keys=@(Read-ReviewSql $Mysql $Client "SELECT COLUMN_NAME FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA='alshifa' AND TABLE_NAME='$table' AND CONSTRAINT_NAME='PRIMARY' ORDER BY ORDINAL_POSITION" $Private)
        $generated=@(Read-ReviewSql $Mysql $Client "SELECT COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='alshifa' AND TABLE_NAME='$table' AND GENERATION_EXPRESSION<>'' ORDER BY COLUMN_NAME" $Private)
        $shape=@(Read-ReviewSql $Mysql $Client "SELECT CONCAT_WS('|',COLUMN_NAME,COLUMN_TYPE,IS_NULLABLE,IFNULL(HEX(COLUMN_DEFAULT),'NULL'),EXTRA,IFNULL(COLLATION_NAME,''),GENERATION_EXPRESSION) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='alshifa' AND TABLE_NAME='$table' ORDER BY COLUMN_NAME" $Private)
        $indexes=@(Read-ReviewSql $Mysql $Client "SELECT CONCAT_WS('|',INDEX_NAME,NON_UNIQUE,SEQ_IN_INDEX,COLUMN_NAME,IFNULL(SUB_PART,0),INDEX_TYPE) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA='alshifa' AND TABLE_NAME='$table' ORDER BY INDEX_NAME,SEQ_IN_INDEX" $Private)
        $relations=@(Read-ReviewSql $Mysql $Client "SELECT CONCAT_WS('|',CONSTRAINT_NAME,COLUMN_NAME,ORDINAL_POSITION,IFNULL(REFERENCED_TABLE_NAME,''),IFNULL(REFERENCED_COLUMN_NAME,'')) FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA='alshifa' AND TABLE_NAME='$table' ORDER BY CONSTRAINT_NAME,ORDINAL_POSITION" $Private)
        $layout[$table]=[ordered]@{columns=$columns;keys=$keys;generated=$generated;shapeHash=[AlShifa.SyncReview.Crypto]::TextHash(($shape -join "`n"));indexHash=[AlShifa.SyncReview.Crypto]::TextHash(($indexes -join "`n"));relationHash=[AlShifa.SyncReview.Crypto]::TextHash(($relations -join "`n"))}
    }
    return $layout
}
function Get-ReviewProof([string]$Mysql,[string]$Client,$Layout,[string]$Private) {
    $proof=[ordered]@{}
    foreach($table in $Layout.Keys){
        $expression=Get-ReviewExpression $Layout[$table].columns
        [string[]]$rows=@(Read-ReviewSql $Mysql $Client ("SELECT $expression FROM "+(Quote-ReviewName $table)) $Private)
        [Array]::Sort($rows,[StringComparer]::Ordinal)
        $proof[$table]=[ordered]@{count=$rows.Length;hash=[AlShifa.SyncReview.Crypto]::TextHash(($rows -join "`n"))}
    }
    return $proof
}
function Get-ReviewFingerprints([string]$Mysql,[string]$Client,$Layout,[byte[]]$Key,[string]$Private,$Policy) {
    $tables=[ordered]@{}
    foreach($table in $Layout.Keys){
        if($Policy.modelTables -and $Policy.modelTables -notcontains $table){continue}
        if($Policy.localOnlyTables -contains $table){continue}
        $tableKey=$table.ToLowerInvariant()
        $logicalName=if($Policy.modelTables){@($Policy.modelTables|Where-Object {$_ -ieq $table})[0]}else{$table}
        $excluded=@($Layout[$table].generated)
        if($Policy.localOnlyColumns.PSObject.Properties[$table]){$excluded+=@($Policy.localOnlyColumns.$table)}
        $columns=@($Layout[$table].columns|Where-Object {$excluded -notcontains $_})
        $keys=@($Layout[$table].keys)
        if(-not $keys.Count){throw 'sync_table_without_primary_key'}
        $keyExpression=Get-ReviewExpression $keys
        $rowExpression=Get-ReviewExpression $columns
        $rows=@(Read-ReviewSql $Mysql $Client ("SELECT $keyExpression,$rowExpression FROM "+(Quote-ReviewName $table)) $Private)
        $values=@()
        foreach($row in $rows){
            $pair=$row -split "`t"
            if($pair.Length -ne 2 -or $pair[0] -notmatch '^[a-fA-F0-9]{64}$' -or $pair[1] -notmatch '^[a-fA-F0-9]{64}$'){throw 'fingerprint_format'}
            $values+=@{key=[AlShifa.SyncReview.Crypto]::Mac($Key,"$tableKey`nkey`n$($pair[0].ToLowerInvariant())");row=[AlShifa.SyncReview.Crypto]::Mac($Key,"$tableKey`nrow`n$($pair[1].ToLowerInvariant())")}
        }
        $tables[$logicalName]=[ordered]@{columns=$columns;keys=$keys;rows=$values}
    }
    return $tables
}
function Copy-ReviewTree([string]$Source,[string]$Destination) {
    if(-not(Test-Path -LiteralPath $Source -PathType Container)){throw 'backup_directory_missing'}
    [void][IO.Directory]::CreateDirectory($Destination)
    foreach($item in Get-ChildItem -LiteralPath $Source -Force){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'backup_reparse_path'}
        $target=Join-Path $Destination $item.Name
        if($item.PSIsContainer){Copy-ReviewTree $item.FullName $target}
        else{
            [IO.File]::Copy($item.FullName,$target,$false)
            if((Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash){throw 'file_backup_mismatch'}
        }
    }
}
function Invoke-HospitalSyncReview([string]$PackageDirectory,[string]$ReturnDirectory) {
    $manifest=Get-Content -LiteralPath (Join-Path $PackageDirectory 'package.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $stage='installation_identity';$private=$null;$sourceClient=$null;$cloneClient=$null;$clone=$null;$cloneOut=$null;$cloneErr=$null;$apiStopped=$false;$apiWasRunning=$false;$apiName='AlShifaApi';$lock=$null
    $result=[ordered]@{format='AlShifa.HospitalEvidence.1';fingerprintAlgorithm='mysql-hex-null-delimited-sha256+hmacsha256-lowercase-table-v1';packageId=$manifest.packageId;startedAt=[DateTimeOffset]::UtcNow.ToString('o');success=$false;replicationInvoked=$false;productionDatabaseWrites=$false;keyIssued=$false;sourceIdChanged=$false;configurationChanged=$false;commandsReplayed=$false;activationAuthorized=$false;errorCode=$null}
    try {
        $lock=New-Object Threading.Mutex($false,'Global\AlShifa.HospitalSyncReview')
        try{$owns=$lock.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owns=$true}
        if(-not $owns){throw 'review_already_running'}
        $service=Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
        if(-not $service -or $service.PathName -notmatch '^"?([A-Za-z]:\\[^"\r\n]+\\app\\AlShifa\.Api\.exe)"?$'){throw 'service_path_not_verified'}
        $appExe=$Matches[1];$root=Split-Path (Split-Path $appExe -Parent) -Parent
        [void](Assert-ReviewPath $appExe $root)
        $dbService=Get-CimInstance Win32_Service -Filter "Name='AlShifaMySQL'"
        $mysqld=Join-Path $root 'mysql\bin\mysqld.exe';$mysql=Join-Path $root 'mysql\bin\mysql.exe';$dump=Join-Path $root 'mysql\bin\mysqldump.exe'
        if(-not $dbService -or $dbService.PathName -notmatch ('^"?'+[regex]::Escape($mysqld)+'"?(\s|$)') -or $dbService.State -ne 'Running'){throw 'database_service_not_verified'}
        foreach($path in $mysqld,$mysql,$dump){[void](Assert-ReviewPath $path $root);if(-not(Test-Path -LiteralPath $path -PathType Leaf)){throw 'runtime_missing'}}
        $configPath=Join-Path $root 'app\appsettings.Production.json';[void](Assert-ReviewPath $configPath $root)
        $configBefore=(Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash
        $cfg=Get-Content -LiteralPath $configPath -Raw -Encoding UTF8|ConvertFrom-Json
        $serviceEnv=(Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\AlShifaApi' -Name Environment -ErrorAction SilentlyContinue).Environment
        $overrides=@('ConnectionStrings__Default','Mirror__Enabled','Sync__Url','Sync__Key','Sync__RequireCompatibilityApproval','Sync__IdentityFile','DataProtection__KeysPath','Storage__Path','ASPNETCORE_ENVIRONMENT','DOTNET_ENVIRONMENT')
        foreach($name in $overrides){if([Environment]::GetEnvironmentVariable($name,'Machine') -or [Environment]::GetEnvironmentVariable($name,'Process') -or @($serviceEnv|Where-Object {$_ -like "$name=*"}).Count){throw 'environment_override_requires_review'}}
        if($cfg.Mirror.Enabled -or $cfg.Sync.RequireCompatibilityApproval -ne $true){throw 'compatibility_hold_not_verified'}
        if($cfg.Storage.Path -and [IO.Path]::GetFullPath([string]$cfg.Storage.Path) -ne (Join-Path $root 'storage')){throw 'custom_storage_path_requires_review'}
        if(Test-Path -LiteralPath (Join-Path $root 'keys\update-maintenance')){throw 'another_update_in_progress'}
        $identityPath=if($cfg.Sync.IdentityFile){[string]$cfg.Sync.IdentityFile}else{Join-Path ([string]$cfg.DataProtection.KeysPath) 'sync-installation-id'}
        if([IO.Path]::GetFullPath($identityPath) -ne (Join-Path $root 'keys\sync-installation-id')){throw 'identity_path_requires_review'}
        [void](Assert-ReviewPath $identityPath $root)
        $identity=[IO.File]::ReadAllText($identityPath).Trim()
        if($identity -ne $manifest.installationId){throw 'wrong_hospital_installation'}
        $result.installationId=$identity;$result.compatibilityHold=$true
        $db=New-Object Data.Common.DbConnectionStringBuilder;$db.set_ConnectionString([string]$cfg.ConnectionStrings.Default)
        if($db['Server'] -notin @('localhost','127.0.0.1') -or $db['Database'] -ne 'alshifa'){throw 'source_endpoint_not_local'}
        $sourcePort=[int]$db['Port'];if($sourcePort -le 0 -or $sourcePort -gt 65535){throw 'source_port_invalid'}
        $parent=Join-Path $root 'sync-review-backups';[void](Assert-ReviewPath $parent $root);Protect-ReviewDirectory $parent
        $private=Join-Path $parent ([DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[guid]::NewGuid().ToString('N'))
        Protect-ReviewDirectory $private
        $result.privateBackupPath=$private
        $sourceClient=Join-Path $private 'source-temporary.cnf'
        $user=if($db.ContainsKey('User')){[string]$db['User']}elseif($db.ContainsKey('User ID')){[string]$db['User ID']}else{[string]$db['Uid']}
        New-ReviewClientFile $sourceClient $user ([string]$db['Password']) $sourcePort
        $server=@(Read-ReviewSql $mysql $sourceClient 'SELECT @@port,@@datadir,@@server_uuid;' $private)[0] -split "`t"
        if($server.Length -ne 3 -or [int]$server[0] -ne $sourcePort -or [IO.Path]::GetFullPath($server[1]).TrimEnd('\') -ne (Join-Path $root 'data')){throw 'source_database_identity_mismatch'}
        $result.sourceServerUuid=$server[2]
        $bytes=[long](@(Read-ReviewSql $mysql $sourceClient "SELECT COALESCE(SUM(DATA_LENGTH+INDEX_LENGTH),0) FROM information_schema.TABLES WHERE TABLE_SCHEMA='alshifa'" $private)[0])
        $drive=New-Object IO.DriveInfo([IO.Path]::GetPathRoot($private))
        $filesSize=[long]0
        foreach($folder in 'app','keys','secrets','storage'){
            $source=Join-Path $root $folder;[void](Assert-ReviewPath $source $root)
            if(Test-Path -LiteralPath $source){foreach($item in Get-ChildItem -LiteralPath $source -Recurse -Force){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'backup_reparse_path'};if(-not $item.PSIsContainer){$filesSize+=$item.Length}}}
        }
        if($drive.AvailableFreeSpace -lt [Math]::Max(3GB,($bytes*5+$filesSize*2+1GB))){throw 'insufficient_private_backup_space'}
        $recipientFile=Join-Path $private 'hospital-recipient-private.dpapi'
        $result.hospitalRecipientPublicKey=[AlShifa.SyncReview.Crypto]::NewProtectedKey($recipientFile)
        $result.hospitalRecipientKeyId=[AlShifa.SyncReview.Crypto]::TextHash($result.hospitalRecipientPublicKey)
        $result.fingerprintKey=[Convert]::ToBase64String([AlShifa.SyncReview.Crypto]::Random(32))
        $fingerprintKey=[Convert]::FromBase64String($result.fingerprintKey)
        $result.appExeSha256=(Get-FileHash -LiteralPath $appExe -Algorithm SHA256).Hash.ToLowerInvariant()
        $result.appDllSha256=(Get-FileHash -LiteralPath (Join-Path $root 'app\AlShifa.Api.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
        $result.runtimeVersion=Invoke-ReviewNative $mysqld @('--no-defaults','--version') $private
        $stage='consistent_backup';Write-Output 'STAGE:BACKUP'
        $apiWasRunning=$service.State -eq 'Running'
        if($apiWasRunning){Stop-Service -Name $apiName -ErrorAction Stop;$apiStopped=$true;(Get-Service $apiName).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(60))}
        $active=[int](@(Read-ReviewSql $mysql $sourceClient "SELECT COUNT(*) FROM information_schema.PROCESSLIST WHERE DB='alshifa' AND ID<>CONNECTION_ID() AND COMMAND<>'Sleep'" $private)[0])
        if($active -ne 0){throw 'concurrent_database_activity'}
        $layout=Get-ReviewLayout $mysql $sourceClient $private
        foreach($required in 'SyncStates','SyncChanges','SyncCommands','Patients','Invoices','Payments','LedgerEntries','__EFMigrationsHistory'){if(-not $layout.Contains($required)){throw 'required_table_missing'}}
        foreach($mapped in @($manifest.syncPolicy.modelTables|Where-Object {$_})){if(-not $layout.Contains($mapped)){throw 'mapped_table_missing'}}
        $result.schema=@(Read-ReviewSql $mysql $sourceClient 'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1' $private)[0]
        if($result.schema -ne $manifest.schema){throw 'hospital_schema_mismatch'}
        $before=Get-ReviewProof $mysql $sourceClient $layout $private
        $databaseBackup=Join-Path $private 'database.sql'
        [void](Invoke-ReviewNative $dump @("--defaults-file=$sourceClient",'--single-transaction','--quick','--routines','--events','--triggers','--hex-blob','--no-tablespaces','--set-gtid-purged=OFF',"--result-file=$databaseBackup",'--databases','alshifa') $private)
        foreach($folder in 'app','keys','secrets','storage'){
            $source=Join-Path $root $folder
            if(Test-Path -LiteralPath $source){Copy-ReviewTree $source (Join-Path $private ('files\'+$folder))}
        }
        [IO.File]::WriteAllText((Join-Path $private 'service-configuration-private.json'),(@{api=$service;database=$dbService}|ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
        $result.localSequenceState=@(Read-ReviewSql $mysql $sourceClient "SELECT JSON_OBJECT('key',``Key``,'value',Value,'updatedAt',UpdatedAt) FROM SyncStates WHERE ``Key`` IN ('push.seq','push.ackedSeq','push.snapshotOf','push.enabled','push.lastSuccessAt') ORDER BY ``Key``" $private|ForEach-Object {$_|ConvertFrom-Json})
        $result.queueCount=$before['SyncChanges'].count;$result.queueProof=$before['SyncChanges']
        $result.oldRequestReceipts=@()
        foreach($target in $manifest.receiptTargets){
            if($target.id -notmatch '^[a-fA-F0-9]{64}$'){throw 'invalid_command_identity'}
            $rows=@(Read-ReviewSql $mysql $sourceClient ("SELECT JSON_OBJECT('id',Id,'requestHash',RequestHash,'statusCode',StatusCode,'completedAt',CompletedAt,'baseSeq',BaseSeq) FROM SyncCommands WHERE Id='"+$target.id+"'") $private)
            $result.oldRequestReceipts+=@{id=$target.id;found=($rows.Count -eq 1);metadata=if($rows.Count -eq 1){$rows[0]|ConvertFrom-Json}else{$null}}
        }
        $result.tables=Get-ReviewFingerprints $mysql $sourceClient $layout $fingerprintKey $private $manifest.syncPolicy
        $result.pendingKeys=@()
        foreach($row in @(Read-ReviewSql $mysql $sourceClient 'SELECT DISTINCT TableName FROM SyncChanges ORDER BY TableName' $private)){
            if(-not $result.tables.Contains($row)){throw 'queue_contains_unknown_or_excluded_table'}
            $tableKey=$row.ToLowerInvariant()
            $keyExpression=Get-ReviewExpression $layout[$row].keys -JsonKey
            $query="SELECT Id,$keyExpression,JSON_LENGTH(KeyJson) FROM SyncChanges WHERE TableName='$row' ORDER BY Id"
            foreach($item in @(Read-ReviewSql $mysql $sourceClient $query $private)){
                $parts=$item -split "`t"
                if($parts.Length -ne 3 -or $parts[1] -notmatch '^[a-fA-F0-9]{64}$' -or [int]$parts[2] -ne $layout[$row].keys.Count){throw 'queue_key_invalid'}
                $result.pendingKeys+=@{id=[long]$parts[0];table=$row;key=[AlShifa.SyncReview.Crypto]::Mac($fingerprintKey,"$tableKey`nkey`n$($parts[1].ToLowerInvariant())")}
            }
        }
        if($result.pendingKeys.Count -ne $result.queueCount){throw 'queue_fingerprint_incomplete'}
        $after=Get-ReviewProof $mysql $sourceClient $layout $private
        if(($before|ConvertTo-Json -Depth 12 -Compress) -cne ($after|ConvertTo-Json -Depth 12 -Compress)){throw 'source_changed_during_backup'}
        $result.sourceStableDuringBackup=$true
        $result.databaseBackupSha256=(Get-FileHash -LiteralPath $databaseBackup -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText((Join-Path $private 'source-proof-private.json'),(@{layout=$layout;rows=$before}|ConvertTo-Json -Depth 18),(New-Object Text.UTF8Encoding($false)))
        if($apiStopped){Start-Service $apiName -ErrorAction Stop;(Get-Service $apiName).WaitForStatus('Running',[TimeSpan]::FromSeconds(60));$apiStopped=$false}
        $stage='isolated_restore';Write-Output 'STAGE:RESTORE'
        $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$listener.Start();$clonePort=([Net.IPEndPoint]$listener.LocalEndpoint).Port;$listener.Stop()
        if($clonePort -eq $sourcePort -or $clonePort -eq 3308){throw 'clone_port_conflict'}
        $cloneData=Join-Path $private 'clone-data';[void](Assert-ReviewPath $cloneData $private)
        if(Test-Path -LiteralPath $cloneData){throw 'clone_path_exists'}
        $baseDir=Split-Path (Split-Path $mysqld -Parent) -Parent
        [void](Invoke-ReviewNative $mysqld @('--no-defaults','--initialize-insecure',"--basedir=$baseDir","--datadir=$cloneData",'--console') $private $null 180000)
        $clonePassword=[AlShifa.SyncReview.Crypto]::Hash([AlShifa.SyncReview.Crypto]::Random(32))
        $cloneClient=Join-Path $private 'clone-private.cnf';New-ReviewClientFile $cloneClient 'root' $clonePassword $clonePort
        $init=Join-Path $private 'clone-init-private.sql';[IO.File]::WriteAllText($init,"ALTER USER 'root'@'localhost' IDENTIFIED BY '$clonePassword';")
        $cloneArgs=@('--no-defaults','--no-monitor',"--basedir=$baseDir","--datadir=$cloneData",'--bind-address=127.0.0.1',"--port=$clonePort",'--mysqlx=OFF','--skip-log-bin','--event-scheduler=OFF','--local-infile=OFF','--secure-file-priv=NULL',"--init-file=$init",'--console')
        $clone=New-Object Diagnostics.Process;$clone.StartInfo=[AlShifa.SyncReview.Native]::Info($mysqld,$cloneArgs);[void]$clone.Start();$clone.StandardInput.Close();$cloneOut=$clone.StandardOutput.ReadToEndAsync();$cloneErr=$clone.StandardError.ReadToEndAsync()
        $ready=$false
        for($i=0;$i -lt 90;$i++){
            if($clone.HasExited){throw 'clone_exited'}
            try{
                $identitySql='SELECT @@port,@@datadir,@@server_uuid;'
                $out=Invoke-ReviewNative $mysql @("--defaults-file=$cloneClient",'-N','-B','-e',$identitySql) $private $null 5000
                $actual=$out.Trim() -split "`t"
                if($actual.Length -eq 3 -and [int]$actual[0] -eq $clonePort -and [IO.Path]::GetFullPath($actual[1]).TrimEnd('\') -eq $cloneData -and $actual[2] -ne $server[2]){$ready=$true;break}
            }catch{}
            Start-Sleep -Seconds 1
        }
        if(-not $ready){throw 'clone_identity_not_verified'}
        $endpoint=@(Get-NetTCPConnection -State Listen -LocalPort $clonePort -ErrorAction Stop)
        if(-not $endpoint.Count -or @($endpoint|Where-Object {$_.LocalAddress -ne '127.0.0.1' -or $_.OwningProcess -ne $clone.Id}).Count){throw 'clone_listener_not_private'}
        [void](Invoke-ReviewNative $mysql @("--defaults-file=$cloneClient",'--binary-mode') $private $databaseBackup)
        $restoredLayout=Get-ReviewLayout $mysql $cloneClient $private
        if(($restoredLayout|ConvertTo-Json -Depth 18 -Compress) -cne ($layout|ConvertTo-Json -Depth 18 -Compress)){throw 'restored_schema_differs'}
        $restored=Get-ReviewProof $mysql $cloneClient $layout $private
        if(($restored|ConvertTo-Json -Depth 12 -Compress) -cne ($before|ConvertTo-Json -Depth 12 -Compress)){throw 'restored_rows_differ'}
        $result.backupRestoreVerified=$true;$result.restoredTableCount=$layout.Count;$result.cloneNetwork='127.0.0.1-only';$result.cloneServerIdentityVerified=$true
        $result.unbalancedLedgerTransactions=[int](@(Read-ReviewSql $mysql $cloneClient 'SELECT COUNT(*) FROM (SELECT TransactionId FROM LedgerEntries GROUP BY TransactionId HAVING SUM(Debit)<>SUM(Credit)) t' $private)[0])
        $missing=0;$mismatched=0
        foreach($attachment in @(Read-ReviewSql $mysql $cloneClient 'SELECT StoredName,Sha256 FROM Attachments' $private)){
            $pair=$attachment -split "`t"
            if($pair.Length -ne 2 -or $pair[0] -notmatch '^[a-fA-F0-9]{32}$'){$mismatched++;continue}
            $file=Join-Path $private ('files\storage\'+$pair[0])
            if(-not(Test-Path -LiteralPath $file)){$missing++}
            elseif((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $pair[1]){$mismatched++}
        }
        $result.attachmentsMissing=$missing;$result.attachmentsMismatched=$mismatched
        $result.existingBackupInventory=@()
        foreach($folder in 'backups','archives'){
            $path=Join-Path $root $folder
            if(Test-Path -LiteralPath $path){foreach($item in Get-ChildItem -LiteralPath $path -File|Where-Object {$_.Name -match '\.(gpg|zip|sql|bak|gz)$'}|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 12){$result.existingBackupInventory+=@{name=$item.Name;bytes=$item.Length;updatedAt=$item.LastWriteTimeUtc.ToString('o')}}}
        }
        $result.metadataChecksPassed=($result.unbalancedLedgerTransactions -eq 0 -and $missing -eq 0 -and $mismatched -eq 0)
        $result.networkCheck=[ordered]@{onlineTlsReachable=$false;loginStatus=$null;signedStateChecked=$false}
        try{
            $request=[Net.HttpWebRequest]::Create('https://shafa.novatechsoft.com/login');$request.AllowAutoRedirect=$false;$request.Timeout=15000
            $response=$request.GetResponse()
            try{$result.networkCheck.loginStatus=[int]$response.StatusCode;$result.networkCheck.onlineTlsReachable=([int]$response.StatusCode -eq 200)}finally{$response.Close()}
        }catch{}
        $result.apiHealthyAfterRestart=$false
        if($apiWasRunning -and $cfg.Kestrel.Endpoints.Loopback.Url){
            $uri=[uri]$cfg.Kestrel.Endpoints.Loopback.Url
            if($uri.Host -eq '127.0.0.1' -and $uri.Scheme -eq 'http' -and -not $uri.UserInfo -and -not $uri.Query -and -not $uri.Fragment){
                for($i=0;$i -lt 15;$i++){try{$health=Invoke-WebRequest -UseBasicParsing -Uri ($uri.GetLeftPart([UriPartial]::Authority)+'/health') -TimeoutSec 2;if($health.StatusCode -eq 200 -and $health.Content.Trim() -eq 'Healthy'){$result.apiHealthyAfterRestart=$true;break}}catch{};Start-Sleep -Seconds 1}
            }
        }
        if($apiWasRunning -and -not $result.apiHealthyAfterRestart){throw 'api_health_not_verified'}
        $result.onlineReconciliationRequired=$true;$result.success=$true
    } catch {
        $result.errorCode=$stage+'_failed'
        $result.failureType=$_.Exception.GetType().FullName
        $result.failureDetailCode=$null
        $knownCodes=@('service_path_not_verified','database_service_not_verified','runtime_missing','environment_override_requires_review','compatibility_hold_not_verified','custom_storage_path_requires_review','another_update_in_progress','identity_path_requires_review','wrong_hospital_installation','source_endpoint_not_local','source_port_invalid','source_database_identity_mismatch','insufficient_private_backup_space','concurrent_database_activity','required_table_missing','mapped_table_missing','hospital_schema_mismatch','invalid_command_identity','queue_contains_unknown_or_excluded_table','queue_key_invalid','queue_fingerprint_incomplete','source_changed_during_backup','clone_port_conflict','clone_path_exists','clone_exited','clone_identity_not_verified','clone_listener_not_private','restored_schema_differs','restored_rows_differ','native_operation_failed','review_already_running','review_reparse_path','review_path_escape','backup_reparse_path','file_backup_mismatch')
        if($knownCodes -ccontains $_.Exception.Message){$result.failureDetailCode=$_.Exception.Message}
        $diagnostic=if($private){Join-Path $private 'failure-private.txt'}else{Join-Path $PackageDirectory ('preflight-private-'+[guid]::NewGuid().ToString('N')+'.txt')}
        [IO.File]::WriteAllText($diagnostic,$_.Exception.ToString())
    } finally {
        if($apiStopped){try{Start-Service $apiName -ErrorAction Stop;(Get-Service $apiName).WaitForStatus('Running',[TimeSpan]::FromSeconds(60));$apiStopped=$false}catch{$result.errorCode='api_restart_failed';$result.success=$false}}
        $result.apiRestarted=($apiWasRunning -and -not $apiStopped)
        if($clone){try{if(-not $clone.HasExited){$clone.Kill();$clone.WaitForExit(30000)|Out-Null};$result.privateCloneStopped=$clone.HasExited;if($cloneErr -and $private){[IO.File]::WriteAllText((Join-Path $private 'clone-private.log'),$cloneErr.GetAwaiter().GetResult())}}catch{$result.privateCloneStopped=$false;$result.success=$false};$clone.Dispose()}
        if($sourceClient -and (Test-Path -LiteralPath $sourceClient)){Remove-Item -LiteralPath $sourceClient -Force}
        if($private -and $configBefore){$result.configurationPreserved=((Get-FileHash -LiteralPath $configPath -Algorithm SHA256).Hash -eq $configBefore);if(-not $result.configurationPreserved){$result.success=$false;$result.errorCode='configuration_changed_externally'}}
        if($lock -and $owns){$lock.ReleaseMutex()};if($lock){$lock.Dispose()}
    }
    $stage='encrypted_return';Write-Output 'STAGE:ENCRYPT'
    $result.finishedAt=[DateTimeOffset]::UtcNow.ToString('o')
    $officePublic=[IO.File]::ReadAllText((Join-Path $PackageDirectory 'office-public.xml'))
    $payload=[Text.Encoding]::UTF8.GetBytes(($result|ConvertTo-Json -Depth 30 -Compress))
    $sealed=[AlShifa.SyncReview.Crypto]::Seal($officePublic,$manifest.packageId,$payload)
    if(-not(Test-Path -LiteralPath $ReturnDirectory)){[void][IO.Directory]::CreateDirectory($ReturnDirectory)}
    $return=Join-Path $ReturnDirectory ('hospital-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')+'-'+[guid]::NewGuid().ToString('N')+'.alshifa-review')
    [IO.File]::WriteAllText($return,$sealed,(New-Object Text.UTF8Encoding($false)))
    if($private){[IO.File]::WriteAllText((Join-Path $private 'return-envelope.alshifa-review'),$sealed,(New-Object Text.UTF8Encoding($false)))}
    if($result.success -and $result.metadataChecksPassed -and $result.backupRestoreVerified -and $result.apiHealthyAfterRestart){Write-Output 'RESULT:COLLECTED';return 0}
    if($result.success){Write-Output 'RESULT:HELD:local_safety_checks_require_review';return 2}
    Write-Output ('RESULT:HELD:'+ $result.errorCode);return 2
}
