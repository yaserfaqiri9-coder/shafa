# Two-host test: a disposable local MySQL instance and a dedicated Contabo MySQL container/API.
# No production service, database, reverse proxy, firewall or DNS configuration is changed.
param([string]$SshKey = 'C:\Users\PARSA COMPUTER\.ssh\codex_contabo_deploy')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')
$nonce = [Guid]::NewGuid().ToString('N')
$local = Join-Path $repo ('.tools\contabo-sync-' + $nonce)
$remote = '/var/tmp/alshifa-sync-lab-' + $nonce
$container = 'alshifa-sync-test-' + $nonce
$unit = 'alshifa-sync-test-' + $nonce
$sshTarget = 'root@157.173.109.255'
$sshArgs = @('-4','-o','IPQoS=none','-o','BatchMode=yes','-o','ConnectTimeout=10','-o','StrictHostKeyChecking=yes','-i',$SshKey)
New-Item -ItemType Directory $local | Out-Null
Protect-Path $local @("$([Security.Principal.WindowsIdentity]::GetCurrent().Name)=F")
foreach ($port in 33443,33480) { if (-not (Test-PortFree $port)) { throw 'A required isolated tunnel port is occupied.' } }
$rootPassword = New-Secret; $writerPassword = New-Secret; $readerPassword = New-Secret; $syncKey = New-Secret 48
function Remote([string]$Command) { Invoke-Native 'ssh.exe' ($sshArgs + @($sshTarget,$Command)) }
function Upload([string]$Path,[string]$Name) { Invoke-Native 'scp.exe' ($sshArgs + @($Path, ($sshTarget + ':' + $remote + '/' + $Name))) | Out-Null }
$tunnel = $null
$created = $false
$testExit = 1
$oldLab = $env:ALSHIFA_REMOTE_LAB_FILE
try {
    # Always build current source; never reuse a prior release whose protocol or migrations may differ.
    $app = Join-Path $local 'publish\app'
    Invoke-Native (Join-Path $repo '.tools\dotnet\dotnet.exe') @('publish', (Join-Path $repo 'backend\src\AlShifa.Api\AlShifa.Api.csproj'), '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true', '--artifacts-path', (Join-Path $local 'build'), '-o', $app, '--nologo', '-v', 'quiet') | Out-Null
    Remote "if ss -ltnH 'sport = :33381 or sport = :33443' | grep -q .; then exit 1; fi" | Out-Null
    # All remote names and paths contain only a fixed prefix and a random hexadecimal identifier.
    Remote "test ! -e $remote && install -d -m 700 $remote && test -f /etc/letsencrypt/live/shafa.novatechsoft.com/fullchain.pem && test -f /etc/letsencrypt/live/shafa.novatechsoft.com/privkey.pem" | Out-Null
    $created = $true
    if (-not (Test-Path -LiteralPath (Join-Path $app 'AlShifa.Api'))) { throw 'Publish the current linux-x64 API first.' }
    $archive = Join-Path $local 'app.tar.gz'
    Invoke-Native 'tar.exe' @('-czf',$archive,'--exclude=appsettings*.json','--exclude=keys','--exclude=storage','--exclude=logs','-C',(Split-Path $app -Parent),'app') | Out-Null
    Upload $archive 'app.tar'
    $archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Remote "echo '$archiveHash  $remote/app.tar' | sha256sum --check --status" | Out-Null
    [IO.File]::WriteAllText((Join-Path $local 'mysql.env'), "MYSQL_ROOT_PASSWORD=$rootPassword`nMYSQL_ROOT_HOST=%`nMYSQL_DATABASE=alshifa_remote_test`n")
    [IO.File]::WriteAllText((Join-Path $local 'root.cnf'), "[client]`nuser=root`npassword=$rootPassword`nhost=127.0.0.1`nport=3306`nprotocol=TCP`n")
    $sql = "CREATE USER 'lab_owner'@'%' IDENTIFIED BY '$writerPassword';`nGRANT ALL PRIVILEGES ON alshifa_remote_test.* TO 'lab_owner'@'%';`nCREATE USER 'lab_ro'@'%' IDENTIFIED BY '$readerPassword';`nGRANT SELECT ON alshifa_remote_test.* TO 'lab_ro'@'%';`n"
    [IO.File]::WriteAllText((Join-Path $local 'accounts.sql'), $sql)
    [IO.File]::WriteAllText((Join-Path $local 'grants.sql'), "GRANT UPDATE (AccessFailedCount, LockoutEnd, ConcurrencyStamp) ON alshifa_remote_test.AspNetUsers TO 'lab_ro'@'%';")
    $writer = "Server=127.0.0.1;Port=33381;Database=alshifa_remote_test;User=lab_owner;Password=$writerPassword;"
    $reader = "Server=127.0.0.1;Port=33381;Database=alshifa_remote_test;User=lab_ro;Password=$readerPassword;"
    $config = @{
        ConnectionStrings=@{ Default=$reader }; Mirror=@{Enabled=$true; SyncKey=$syncKey; SyncConnection=$writer}
        DataProtection=@{KeysPath="$remote/keys"}; Storage=@{Path="$remote/storage"}
        RateLimit=@{LoginPerMinute=1000}; Logging=@{LogLevel=@{Default='Warning'}}
        Kestrel=@{Endpoints=@{Lab=@{Url='https://127.0.0.1:33443';Certificate=@{Path='/etc/letsencrypt/live/shafa.novatechsoft.com/fullchain.pem';KeyPath='/etc/letsencrypt/live/shafa.novatechsoft.com/privkey.pem'}}}}
    }
    [IO.File]::WriteAllText((Join-Path $local 'appsettings.Production.json'), ($config | ConvertTo-Json -Depth 12))
    # This is a shell-readable environment file with values generated solely for this disposable lab.
    [IO.File]::WriteAllText((Join-Path $local 'owner.env'), "ConnectionStrings__Default='$writer'`nASPNETCORE_ENVIRONMENT='Production'`n")
    foreach ($file in 'mysql.env','root.cnf','accounts.sql','grants.sql','appsettings.Production.json','owner.env') { Upload (Join-Path $local $file) $file }
    Remote "chmod 600 $remote/*.env $remote/*.cnf $remote/*.sql $remote/*.json; chmod 644 $remote/accounts.sql; tar -xf $remote/app.tar -C $remote; chmod +x $remote/app/AlShifa.Api; install -d -m 700 $remote/mysql $remote/keys $remote/storage; docker run -d --name $container --label alshifa.test=$nonce --network bridge -p 127.0.0.1:33381:3306 --env-file $remote/mysql.env -v $remote/mysql:/var/lib/mysql -v ${remote}:/lab:ro -v $remote/accounts.sql:/docker-entrypoint-initdb.d/10-accounts.sql:ro mysql:8.4 > $remote/container.id" | Out-Null
    $ready=$false
    for ($attempt=0;$attempt -lt 60;$attempt++) {
        try { Remote "docker exec $container mysqladmin --defaults-extra-file=/lab/root.cnf ping --silent" | Out-Null; $ready=$true; break }
        catch { Start-Sleep -Seconds 1 }
    }
    if (-not $ready) { throw 'Dedicated remote MySQL was not ready.' }
    # Verify the container owns only this lab's data mount and binds only loopback before any schema command.
    $inspection = (Remote "docker inspect $container") -join "`n" | ConvertFrom-Json
    if ($inspection.Config.Labels.'alshifa.test' -ne $nonce -or @($inspection.Mounts | Where-Object { $_.Destination -eq '/var/lib/mysql' -and $_.Source -eq "$remote/mysql" }).Count -ne 1 -or $inspection.HostConfig.PortBindings.'3306/tcp'[0].HostIp -ne '127.0.0.1') { throw 'Remote container isolation verification failed.' }
    # The new test schema has no operational data. Retain its baseline before migration.
    Remote "docker exec $container mysqldump --defaults-extra-file=/lab/root.cnf --single-transaction --no-tablespaces --set-gtid-purged=OFF alshifa_remote_test > $remote/before-migration.sql && test -s $remote/before-migration.sql" | Out-Null
    Remote "cd $remote && set -a && . ./owner.env && set +a && ./app/AlShifa.Api --migrate --contentRoot $remote > $remote/migrate.log 2>&1" | Out-Null
    # Prove that the freshly migrated synthetic schema can be dumped and restored on this same isolated instance.
    [IO.File]::WriteAllText((Join-Path $local 'restore.sql'), "CREATE DATABASE alshifa_remote_restore_test;")
    Upload (Join-Path $local 'restore.sql') 'restore.sql'
    Remote "docker exec $container mysqldump --defaults-extra-file=/lab/root.cnf --single-transaction --no-tablespaces --set-gtid-purged=OFF alshifa_remote_test > $remote/migrated-schema.sql && docker exec $container mysql --defaults-extra-file=/lab/root.cnf -e 'source /lab/restore.sql' && docker exec $container mysql --defaults-extra-file=/lab/root.cnf alshifa_remote_restore_test -e 'source /lab/migrated-schema.sql'" | Out-Null
    $schemaCountSql = "SELECT TABLE_SCHEMA,COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA IN ('alshifa_remote_test','alshifa_remote_restore_test') GROUP BY TABLE_SCHEMA;"
    [IO.File]::WriteAllText((Join-Path $local 'verify-restore.sql'), $schemaCountSql)
    Upload (Join-Path $local 'verify-restore.sql') 'verify-restore.sql'
    $restoredCounts = @(Remote "docker exec $container mysql --defaults-extra-file=/lab/root.cnf -N -B -e 'source /lab/verify-restore.sql'")
    if ($restoredCounts.Count -ne 2 -or ($restoredCounts[0] -split "`t")[-1] -ne ($restoredCounts[1] -split "`t")[-1]) { throw 'Synthetic schema restore did not preserve table count.' }
    Remote "docker exec $container mysql --defaults-extra-file=/lab/root.cnf -e 'source /lab/grants.sql' && systemd-run --quiet --unit=$unit --property=WorkingDirectory=$remote --property=Environment=ASPNETCORE_ENVIRONMENT=Production --property=StandardOutput=file:$remote/api.log --property=StandardError=file:$remote/api.err $remote/app/AlShifa.Api --contentRoot $remote" | Out-Null
    $uuid = ((Remote "docker exec $container mysql --defaults-extra-file=/lab/root.cnf -N -B -e 'SELECT @@server_uuid;'") -join '').Trim()
    if ($uuid -notmatch '^[a-f0-9-]{36}$') { throw 'Remote instance UUID was not verified.' }
    $tunnelArguments = $sshArgs + @('-N','-T','-o','ExitOnForwardFailure=yes','-L','127.0.0.1:33443:127.0.0.1:33443','-L','127.0.0.1:33480:127.0.0.1:33381',$sshTarget)
    # Start-Process needs explicit quoting for the local key path containing spaces.
    $tunnelArguments = @($tunnelArguments | ForEach-Object { if ($_ -match ' ') { '"' + $_ + '"' } else { $_ } })
    $tunnel = Start-Process -FilePath (Get-Command ssh.exe).Source -ArgumentList $tunnelArguments -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $local 'tunnel.err')
    $remoteSettings = @{ remoteBase='https://shafa.novatechsoft.com:33443'; apiTunnelPort=33443; dbTunnelPort=33480; serverUuid=$uuid; writerConnection="Server=127.0.0.1;Port=33480;Database=alshifa_remote_test;User=lab_owner;Password=$writerPassword;"; syncKey=$syncKey }
    $env:ALSHIFA_REMOTE_LAB_FILE = Join-Path $local 'remote-lab.json'
    [IO.File]::WriteAllText($env:ALSHIFA_REMOTE_LAB_FILE, ($remoteSettings | ConvertTo-Json))
    'Verified separate Contabo MySQL container, test schema, loopback API and SSH tunnels. Production services are unchanged.'
    $testDeadline=[DateTime]::UtcNow.AddMinutes(10)
    while (Get-Process testhost -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -ge $testDeadline) { throw 'Another testhost did not finish; the two-host test was not started.' }
        Start-Sleep -Seconds 5
    }
    & (Join-Path $repo 'scripts\test-automation-isolated.ps1') -Filter 'FullyQualifiedName~RemoteSyncTests' -SkipBackupDrill
    $testExit = $LASTEXITCODE
    if ($testExit -ne 0) { throw 'Two-host Sync test failed; private synthetic evidence is retained.' }
    # Restore the final synthetic patients and finance rows as well, not only an empty schema.
    Remote "docker exec $container mysqldump --defaults-extra-file=/lab/root.cnf --single-transaction --no-tablespaces --set-gtid-purged=OFF alshifa_remote_test > $remote/after-test.sql && docker exec $container mysql --defaults-extra-file=/lab/root.cnf alshifa_remote_restore_test -e 'source /lab/after-test.sql'" | Out-Null
    $checksumTables = @('Patients','Invoices','InvoiceItems','Payments','LedgerEntries','SyncCommands','SyncStates','__EFMigrationsHistory')
    $checksumNames = @('alshifa_remote_test','alshifa_remote_restore_test' | ForEach-Object { $schema=$_; $checksumTables | ForEach-Object { "$schema.$_" } }) -join ','
    [IO.File]::WriteAllText((Join-Path $local 'verify-data.sql'), "CHECKSUM TABLE $checksumNames;")
    Upload (Join-Path $local 'verify-data.sql') 'verify-data.sql'
    $checksumRows = @(Remote "docker exec $container mysql --defaults-extra-file=/lab/root.cnf -N -B -e 'source /lab/verify-data.sql'")
    if ($checksumRows.Count -ne 2*$checksumTables.Count) { throw 'Synthetic backup verification returned an unexpected table set.' }
    for ($i=0;$i -lt $checksumTables.Count;$i++) {
        $first=($checksumRows[$i] -split "`t")[-1]; $restored=($checksumRows[$i+$checksumTables.Count] -split "`t")[-1]
        if ($first -eq 'NULL' -or $first -ne $restored) { throw 'Synthetic backup restore changed protected table content.' }
    }
    [IO.File]::WriteAllText((Join-Path $local 'backup-proof.json'), (@{verifiedAt=[DateTime]::UtcNow.ToString('o'); restoredSyntheticTables=$checksumTables; allChecksumsMatch=$true; productionDataUsed=$false} | ConvertTo-Json))
    'PASS: two-host Contabo Sync test completed over verified TLS.'
} finally {
    $env:ALSHIFA_REMOTE_LAB_FILE = $oldLab
    if ($tunnel -and -not $tunnel.HasExited) { Stop-Process -Id $tunnel.Id }
    if ($created) {
        # Stop only the unit and container whose exact random ownership marker was verified above. Retain lab data for review.
        try { Remote "systemctl stop $unit.service 2>/dev/null || true" | Out-Null } catch { }
        try {
            $labels = ((Remote "docker inspect -f '{{json .Config.Labels}}' $container") -join '') | ConvertFrom-Json
            if ($labels.'alshifa.test' -eq $nonce) { Remote "docker stop $container >/dev/null" | Out-Null }
        } catch { }
    }
    "Test resources stopped; private lab evidence retained at $local and $remote."
}
