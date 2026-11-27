# Recovery uses an existing hospital installation as its source. Never returns row values.
function Get-RecoveryContentProof([string]$MySql,[string]$Cnf,[int]$Port) {
    function Read-RecoverySql([string]$Sql) {
        @(Invoke-Native $MySql @("--defaults-extra-file=$Cnf",'--protocol=TCP','--host=127.0.0.1',"--port=$Port",'--batch','--skip-column-names','--default-character-set=utf8mb4','-e',$Sql))
    }
    $tables=@(Read-RecoverySql "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA='alshifa' AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME")
    if(-not $tables.Count){throw 'The recovered database has no business tables.'}
    $allColumns=@(Read-RecoverySql "SELECT TABLE_NAME,COLUMN_NAME FROM information_schema.COLUMNS WHERE TABLE_SCHEMA='alshifa' ORDER BY TABLE_NAME,ORDINAL_POSITION")
    $columnsByTable=@{}
    foreach($line in $allColumns){$pair=$line -split "`t";if($pair.Count -ne 2 -or $pair[0] -notmatch '^[A-Za-z0-9_]+$' -or $pair[1] -notmatch '^[A-Za-z0-9_]+$'){throw 'Unexpected recovery column metadata.'};if($columnsByTable.ContainsKey($pair[0])){$columnsByTable[$pair[0]]+=@($pair[1])}else{$columnsByTable[$pair[0]]=@($pair[1])}}
    $statements=New-Object 'System.Collections.Generic.List[string]'
    foreach($table in $tables){
        if($table -notmatch '^[A-Za-z0-9_]+$'){throw 'Unexpected recovery table identifier.'}
        $columns=@($columnsByTable[$table])
        $parts=@($columns|ForEach-Object {if($_ -notmatch '^[A-Za-z0-9_]+$'){throw 'Unexpected recovery column identifier.'};"IFNULL(CONCAT('V',HEX(CAST(``$_`` AS BINARY))),'N')"})
        $hash="SHA2(CONCAT_WS('|',$($parts -join ',')),256)"
        $sums=@(1,17,33,49|ForEach-Object {"COALESCE(SUM(CAST(CONV(SUBSTRING(h,$_,16),16,10) AS DECIMAL(65,0))),0)"})
        $statements.Add("SELECT '$table',COUNT(*),$($sums -join ',') FROM (SELECT $hash AS h FROM ``alshifa``.``$table``) p;")
    }
    # Batch bounded SQL strings to avoid starting a client for every table/column.
    $results=@{};$batch=''
    foreach($statement in @($statements)+@('')){
        if($batch -and (-not $statement -or ($batch.Length+$statement.Length) -gt 16000)){
            foreach($line in @(Read-RecoverySql $batch)){
                $pair=$line -split "`t",2
                if($pair.Count -ne 2 -or $tables -notcontains $pair[0] -or $results.ContainsKey($pair[0]) -or $pair[1] -notmatch '^\d+(\t\d+){4}$'){throw 'Recovery content verification failed.'}
                $results[$pair[0]]=$pair[1]
            }
            $batch=''
        }
        if($statement.Length -gt 16000){throw 'A recovery table exceeds the supported verification size.'}
        $batch+=$statement
    }
    if($results.Count -ne $tables.Count){throw 'Recovery content verification is incomplete.'}
    foreach($table in $tables){$table+'|'+(@($columnsByTable[$table]) -join ',')+'|'+$results[$table]}
}

function Assert-RecoverySource([string]$Source,$Manifest) {
    foreach($relative in @('app\AlShifa.Api.exe','app\AlShifa.Api.dll','app\appsettings.Production.json','mysql\bin\mysqld.exe','keys\sync-installation-id','secrets\backup-password.txt','secrets\mysql-root.txt','secrets\backup.cnf','secrets\restore.cnf','certs\server.pfx')){
        if(-not(Test-Path -LiteralPath (Join-Path $Source $relative) -PathType Leaf)){throw 'The original installation copy is incomplete.'}
    }
    foreach($item in @($Manifest.sourceFiles)){
        if($item.path -notin @('app\AlShifa.Api.dll','app\appsettings.Production.json','mysql\bin\mysqld.exe','keys\sync-installation-id')){throw 'Unexpected recovery source mapping.'}
        if((Get-FileHash -LiteralPath (Join-Path $Source $item.path)).Hash -cne $item.sha256){throw 'This package does not match the selected original installation.'}
    }
    if(@($Manifest.sourceFiles).Count -ne 4 -or @($Manifest.sourceFiles.path|Select-Object -Unique).Count -ne 4){throw 'The recovery source manifest is incomplete.'}
    $cfg=Get-Content -LiteralPath (Join-Path $Source 'app\appsettings.Production.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $db=New-Object System.Data.Common.DbConnectionStringBuilder
    $db.set_ConnectionString([string]$cfg.ConnectionStrings.Default)
    if($cfg.Mirror.Enabled -or $db['Server'] -notin @('127.0.0.1','localhost') -or $db['Database'] -ne 'alshifa' -or [int]$db['Port'] -eq 3308){throw 'This source needs a separately reviewed recovery mapping.'}
    foreach($folder in 'app','mysql','storage','certs','keys','secrets'){
        $path=Join-Path $Source $folder
        if(-not(Test-Path -LiteralPath $path -PathType Container) -or @(Get-ChildItem -LiteralPath $path -Recurse -Force|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Recovery requires complete local folders without directory links.'}
    }
    $cfg
}

function Copy-RecoveryRuntime([string]$Source,[string]$Root) {
    foreach($folder in 'app','mysql','storage','certs','keys','secrets'){
        $from=Join-Path $Source $folder
        New-Item -ItemType Directory -Force (Join-Path $Root $folder)|Out-Null
        foreach($file in Get-ChildItem -LiteralPath $from -File -Recurse -Force){
            # Static link libraries and debug symbols are not runtime dependencies.
            if($folder -eq 'mysql' -and $file.Extension -in @('.pdb','.lib')){continue}
            $relative=$file.FullName.Substring($from.Length).TrimStart('\')
            $target=Join-Path (Join-Path $Root $folder) $relative
            New-Item -ItemType Directory -Force (Split-Path $target -Parent)|Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
}

function Set-RecoverySyncRetryTask([string]$Root) {
    $name='AlShifa Recovery Sync Resume'
    $script=Join-Path $Root 'scripts\resume-recovered-sync.ps1'
    $existing=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if($existing -and $existing.Actions.Arguments -notlike ('*"'+$script+'"*')){throw 'An unrelated recovery task already exists.'}
    $arguments="-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$script`" -Root `"$Root`""
    $triggers=@((New-ScheduledTaskTrigger -AtStartup),(New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) -RepetitionInterval (New-TimeSpan -Minutes 5)))
    $settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 10)
    Register-ScheduledTask -TaskName $name -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $arguments) -Trigger $triggers -Settings $settings -User SYSTEM -RunLevel Highest -Force|Out-Null
}
