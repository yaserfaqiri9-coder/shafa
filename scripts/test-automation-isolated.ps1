# Disposable loopback-only MySQL instance. Never loads dev-env.ps1 or installed hospital configuration.
param([string]$Filter = 'FullyQualifiedName~AlShifa.Tests.SyncTests|FullyQualifiedName~AutomationUnitTests|FullyQualifiedName~SoftwareUpdateTests|FullyQualifiedName~PilotReadinessTests|FullyQualifiedName~HospitalReadinessTests|FullyQualifiedName~FinanceTests|FullyQualifiedName~ConcurrencyTests|FullyQualifiedName~AuthTests|FullyQualifiedName~MigrationSafetyTests|FullyQualifiedName~ReceptionTests|FullyQualifiedName~VisitFlowTests|FullyQualifiedName~AdmissionFlowTests', [switch]$SkipBackupDrill, [switch]$ProofOnly)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')
$labMutex = New-Object System.Threading.Mutex($false, 'Local\AlShifa.IsolatedVerification')
try { $ownsLab = $labMutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $ownsLab = $true }
if (-not $ownsLab) { $labMutex.Dispose(); throw 'Another isolated browser/database lab owns the verification lock.' }
if (Get-Process testhost -ErrorAction SilentlyContinue) { throw 'Another testhost is active; do not overlap database tests.' }
$lab = Join-Path $repo ('.tools\automation-lab-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $lab | Out-Null
Protect-Path $lab @("$([Security.Principal.WindowsIdentity]::GetCurrent().Name)=F")
$bin = Join-Path $repo '.tools\mysql-8.4.11-winx64\bin'
$port = Select-FreePort @(33379..33399) 'isolated automation tests'
$dbPassword = New-Secret
$backupPassword = New-Secret
$restorePassword = New-Secret
$sourceDb = 'alshifa_automation_test'
$data = Join-Path $lab 'data'
$ini = Join-Path $lab 'mysql.ini'
$init = Join-Path $lab 'initialize.sql'
$cnf = Join-Path $lab 'root.cnf'
Get-MySqlIni (Split-Path $bin -Parent) $data (Join-Path $lab 'mysql.err') $port | Set-Content -LiteralPath $ini -Encoding Ascii
Get-MySqlBootstrapSql $sourceDb $dbPassword (New-Secret) $backupPassword $restorePassword | Set-Content -LiteralPath $init -Encoding Ascii
Write-ClientCnf $cnf 'root' $dbPassword $port
$mysqlProcess = $null
$succeeded = $false
$oldTestDb = $env:ALSHIFA_TEST_DB
$oldAdminDb = $env:ALSHIFA_TEST_ADMIN_DB
$oldQueueTestGuard = $env:ALSHIFA_ISOLATED_QUEUE_TESTS
try {
    Invoke-Native (Join-Path $bin 'mysqld.exe') @("--defaults-file=$ini", '--initialize-insecure') | Out-Null
    $mysqlProcess = Start-Process -FilePath (Join-Path $bin 'mysqld.exe') -ArgumentList @("--defaults-file=`"$ini`"", "--init-file=`"$init`"") -WindowStyle Hidden -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($mysqlProcess.HasExited) { throw 'The isolated MySQL process exited before readiness; no database tests were run.' }
        try {
            $identity = @(Invoke-Native (Join-Path $bin 'mysql.exe') @("--defaults-extra-file=$cnf", '-N', '-B', '--raw', '-e', 'SELECT @@port, @@datadir;'))
            $parts = $identity[-1] -split "`t"
            if ([int]$parts[0] -ne $port -or [IO.Path]::GetFullPath($parts[1]).TrimEnd('\') -ne [IO.Path]::GetFullPath($data).TrimEnd('\')) { throw 'Unexpected test server identity.' }
            $listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction Stop
            $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
            # MySQL on Windows can start a monitored child. Verify its exact binary and private configuration.
            if ($listener.LocalAddress -ne '127.0.0.1' -or $owner.ExecutablePath -ne (Join-Path $bin 'mysqld.exe') -or -not $owner.CommandLine.Contains($ini)) { throw 'Unexpected test server listener identity.' }
            $ready = $true; break
        } catch { $readyError = $_.Exception.Message; Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw "Dedicated test server was not verified: $readyError" }
    Remove-Item -LiteralPath $init -Force
    "Verified dedicated MySQL: 127.0.0.1:$port, fresh scratch data directory, owning PID, root privileges confined to this new instance; no concurrent testhost."
    if($ProofOnly){
        . (Join-Path $repo 'deploy\windows\upgrade-safety.ps1')
        $scratch='alshifa_restore_'+[Guid]::NewGuid().ToString('N')
        $sql="CREATE DATABASE $scratch; USE $scratch;"
        foreach($table in 'Patients','Products','StockBatches','StockMovements','StockAdjustments','Payments','AspNetUsers','SyncStates','SyncChanges','SyncCommands','QueueCards','QueueAttendances','QueueDeskLanes'){$sql+="CREATE TABLE $table (Id int PRIMARY KEY, Amount decimal(18,4), NullableValue varchar(20) NULL); INSERT INTO $table VALUES (1,0.1000,NULL),(2,0.1000,NULL);"}
        $mysql=Join-Path $bin 'mysql.exe'
        Invoke-Native $mysql @("--defaults-extra-file=$cnf",'-e',$sql)|Out-Null
        $before=Get-UpgradeDataProof $mysql $cnf $scratch
        Invoke-Native $mysql @("--defaults-extra-file=$cnf",$scratch,'-e','UPDATE Patients SET Amount=0.2000 WHERE Id=1')|Out-Null
        $after=Get-UpgradeDataProof $mysql $cnf $scratch $before.layout
        if(-not @(Compare-Object $before.values $after.values).Count){throw 'Fractional money change was not detected.'}
        Invoke-Native $mysql @("--defaults-extra-file=$cnf",$scratch,'-e',"UPDATE Patients SET Amount=0.1000, NullableValue='' WHERE Id=1")|Out-Null
        $after=Get-UpgradeDataProof $mysql $cnf $scratch $before.layout
        if(-not @(Compare-Object $before.values $after.values).Count){throw 'NULL/empty change was not detected.'}
        Invoke-Native $mysql @("--defaults-extra-file=$cnf",$scratch,'-e','UPDATE Patients SET NullableValue=NULL WHERE Id=1')|Out-Null
        $after=Get-UpgradeDataProof $mysql $cnf $scratch $before.layout
        if(@(Compare-Object $before.values $after.values).Count){throw 'Unchanged data proof was unstable.'}
        foreach($table in 'SyncStates','SyncChanges','SyncCommands','QueueCards','QueueAttendances','QueueDeskLanes','StockAdjustments'){
            Invoke-Native $mysql @("--defaults-extra-file=$cnf",$scratch,'-e',"UPDATE $table SET NullableValue='changed' WHERE Id=1")|Out-Null
            $after=Get-UpgradeDataProof $mysql $cnf $scratch $before.layout
            if(-not @(Compare-Object $before.values $after.values).Count){throw 'Sync checkpoint/queue/receipt mutation was not detected.'}
            Invoke-Native $mysql @("--defaults-extra-file=$cnf",$scratch,'-e',"UPDATE $table SET NullableValue=NULL WHERE Id=1")|Out-Null
        }
        'PASS: upgrade proof detects changes to Sync checkpoints, pending changes and command receipts.'
        'PASS: protected-row proof detects fractional money and NULL/empty changes and accepts identical data.'
        $succeeded = $true
        return
    }
    # A provider migration lock can remain on a pooled session and block other fixture databases.
    # These disposable fixtures deliberately use physical connections whose lifetime ends with their contexts.
    $env:ALSHIFA_ISOLATED_QUEUE_TESTS = '1'
    $env:ALSHIFA_TEST_DB = "Server=127.0.0.1;Port=$port;Database=$sourceDb;User=root;Password=$dbPassword;Pooling=false;"
    $env:ALSHIFA_TEST_ADMIN_DB = "Server=127.0.0.1;Port=$port;Database=alshifa_migration_test;User=root;Password=$dbPassword;Pooling=false;"
    # The Oracle provider uses a server-global migration advisory lock. Its extra
    # connection can outlive a fixture, so independent fixture databases must not
    # migrate concurrently in the same host process.
    $parts=@($Filter.Split('|'))
    $main=@($parts | Where-Object {$_ -notmatch '~AlShifa.Tests.SyncTests(?:\.|$)|~MigrationSafetyTests(?:\.|$)'}) -join '|'
    $groups=@()
    if($main){$groups+= $main}
    foreach($fixture in 'MigrationSafetyTests','AlShifa.Tests.SyncTests') {
        $focused=@($parts | Where-Object {$_ -match ('~'+[regex]::Escape($fixture)+'(?:\.|$)')}) -join '|'
        if($focused){$groups+= $focused}
    }
    $testExit=0; $groupNumber=0
    foreach($group in $groups) {
        $groupNumber++
        & (Join-Path $repo '.tools\dotnet\dotnet.exe') test (Join-Path $repo 'backend\tests\AlShifa.Tests\AlShifa.Tests.csproj') --artifacts-path (Join-Path $repo '.tools\automation-build') --filter $group --logger "trx;LogFileName=automation-$groupNumber.trx" --results-directory (Join-Path $lab 'results') --nologo -v quiet
        if($LASTEXITCODE -ne 0){throw "Focused build/tests failed with exit code $LASTEXITCODE. No later test group or backup drill was run. Inspect $lab\results."}
        [xml]$testResult=Get-Content -LiteralPath (Join-Path $lab "results\automation-$groupNumber.trx") -Raw
        if([int]$testResult.TestRun.ResultSummary.Counters.executed -lt 1){throw 'The filter executed no tests. No backup drill or success result is allowed.'}
    }

    # Exercise the actual encryption, destination verification and isolated restore pipeline using synthetic fixtures only.
    if (-not $SkipBackupDrill) {
    $hostRoot = Join-Path $lab 'host'
    foreach ($folder in 'secrets', 'storage', 'keys', 'app', 'logs') { New-Item -ItemType Directory -Force (Join-Path $hostRoot $folder) | Out-Null }
    Write-ClientCnf (Join-Path $hostRoot 'secrets\backup.cnf') 'alshifa_backup' $backupPassword $port
    Write-ClientCnf (Join-Path $hostRoot 'secrets\restore.cnf') 'alshifa_restore' $restorePassword $port
    [IO.File]::WriteAllText((Join-Path $hostRoot 'secrets\backup-password.txt'), (New-PrintablePassword))
    [IO.File]::WriteAllText((Join-Path $hostRoot 'app\appsettings.Production.json'), '{"Synthetic":true}')
    $attachmentNames = @(Invoke-Native (Join-Path $bin 'mysql.exe') @("--defaults-extra-file=$cnf", '-N', '-B', $sourceDb, '-e', 'SELECT StoredName FROM Attachments;'))
    foreach ($name in $attachmentNames) {
        if ($name -notmatch '^[0-9a-f]{32}$') { throw 'Unexpected synthetic attachment name.' }
        Copy-Item -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) "alshifa-test-storage\$name") -Destination (Join-Path $hostRoot "storage\$name")
    }
    $destination = Join-Path $lab 'archives'
    & (Join-Path $repo 'deploy\windows\backup.ps1') -Root $hostRoot -Destination $destination -Database $sourceDb -MySqlBin $bin -AllowSameDisk
    if (-not $?) { throw 'Synthetic backup drill failed.' }
    $status = Get-Content -LiteralPath (Join-Path $hostRoot 'logs\backup-status.json') -Raw | ConvertFrom-Json
    if (-not $status.lastSuccessAt -or -not $status.restoreVerifiedAt -or $status.lastError) { throw 'Backup did not report verified success.' }
    'PASS: encrypted destination archive restored and verified; settings included; previous archives retained.'
    }
    if ($testExit -ne 0) { throw "Focused tests failed with exit code $testExit. Inspect synthetic TRX results in $lab\results." }
    "PASS: focused integration tests."
    $succeeded = $true
} finally {
    $env:ALSHIFA_TEST_DB = $oldTestDb
    $env:ALSHIFA_TEST_ADMIN_DB = $oldAdminDb
    $env:ALSHIFA_ISOLATED_QUEUE_TESTS = $oldQueueTestGuard
    if ($mysqlProcess -and -not $mysqlProcess.HasExited) {
        try { Invoke-Native (Join-Path $bin 'mysqladmin.exe') @("--defaults-extra-file=$cnf", 'shutdown') | Out-Null }
        catch { Stop-Process -Id $mysqlProcess.Id -ErrorAction SilentlyContinue }
    }
    # A passing run leaves nothing to inspect; each lab holds a full MySQL data directory (~200 MB).
    # A failed run keeps its lab so the TRX results and mysql.err can be read.
    if ($mysqlProcess) { $mysqlProcess.WaitForExit(30000) | Out-Null }
    if ($succeeded) {
        try { Remove-Item -LiteralPath $lab -Recurse -Force -Confirm:$false -ErrorAction Stop; 'Dedicated MySQL stopped and lab removed; no installed database was accessed.' }
        catch { "Dedicated MySQL stopped. Lab could not be removed ($($_.Exception.Message)): $lab" }
    } else {
        "Dedicated MySQL stopped. Lab retained privately for inspection: $lab; no installed database was accessed."
    }
    $labMutex.ReleaseMutex(); $labMutex.Dispose()
}
