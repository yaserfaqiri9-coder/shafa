# Runs deploy\windows\rebuild-database.ps1 against a throwaway MySQL instance with synthetic rows, on its own port and data folder.
# Never touches a Windows service, the development database or any real data. Service control is replaced by a local mysqld process.
# Cases: a successful rebuild (every row identical, damaged directory kept, damage no longer reported) and a failed import
# (the original data directory is put back unchanged).
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$deploy = Join-Path $repo 'deploy\windows'
$mysqlHome = Join-Path $repo '.tools\mysql-8.4.11-winx64'
. (Join-Path $deploy 'common.ps1')
$port = Select-FreePort (33410..33430) 'rebuild test'
$work = Join-Path $repo ('.tools\rebuild-test-' + [Guid]::NewGuid().ToString('N'))
$root = Join-Path $work 'AlShifa'
$scripts = Join-Path $work 'scripts'
foreach ($dir in 'mysql', 'data', 'logs', 'secrets', 'keys', 'app', 'backup-staging') { New-Item -ItemType Directory -Force (Join-Path $root $dir) | Out-Null }
New-Item -ItemType Directory -Force $scripts | Out-Null
cmd /c mklink /J "$(Join-Path $root 'mysql\bin')" "$(Join-Path $mysqlHome 'bin')" | Out-Null

$secrets = @{ Root = New-Secret; App = New-Secret; Backup = New-Secret; Restore = New-Secret }
$ini = Join-Path $root 'mysql\alshifa.ini'
Get-MySqlIni $mysqlHome (Join-Path $root 'data') (Join-Path $root 'logs\mysql.err') $port | Set-Content $ini -Encoding Ascii
Set-Content (Join-Path $root 'secrets\mysql-root.txt') $secrets.Root -Encoding Ascii
Write-ClientCnf (Join-Path $root 'secrets\backup.cnf') 'alshifa_backup' $secrets.Backup $port
Write-ClientCnf (Join-Path $root 'secrets\restore.cnf') 'alshifa_restore' $secrets.Restore $port
$rootCnf = Join-Path $work 'root.cnf'
Write-ClientCnf $rootCnf 'root' $secrets.Root $port
@{ ConnectionStrings = @{ Default = "Server=127.0.0.1;Port=$port;Database=alshifa;User=alshifa;Password=$($secrets.App);" }; DataProtection = @{ KeysPath = (Join-Path $root 'keys') } } |
    ConvertTo-Json -Depth 4 | Set-Content (Join-Path $root 'app\appsettings.Production.json') -Encoding UTF8

# The scripts under test, with service control and ACL changes replaced. Everything else runs as shipped.
foreach ($file in 'rebuild-database.ps1', 'db-integrity.ps1', 'upgrade-safety.ps1') {
    $text = (Get-Content (Join-Path $deploy $file) -Raw) -replace '(?m)^#Requires -RunAsAdministrator\r?\n', ''
    [IO.File]::WriteAllText((Join-Path $scripts $file), $text)
}
$overrides = @"

function Protect-Path { }
function Get-Service([string]`$Name) {
    `$o = [pscustomobject]@{ Name = `$Name }
    `$o | Add-Member ScriptMethod WaitForStatus { param(`$s, `$t) `$deadline = (Get-Date) + `$t; while ((Get-Process mysqld -ErrorAction SilentlyContinue | Where-Object { `$_.Id -eq `$global:RebuildTestPid }) -and (Get-Date) -lt `$deadline) { Start-Sleep -Milliseconds 300 } }
    return `$o
}
function Stop-Service([string]`$Name) {
    if (`$Name -ne 'AlShifaMySQL') { `$global:RebuildTestApi = 'Stopped'; return }
    & '$(Join-Path $mysqlHome 'bin\mysqladmin.exe')' '--defaults-extra-file=$rootCnf' shutdown 2>`$null | Out-Null
    `$p = Get-Process -Id `$global:RebuildTestPid -ErrorAction SilentlyContinue; if (`$p) { `$p.WaitForExit(120000) | Out-Null }
}
function Start-Service([string]`$Name) {
    if (`$Name -ne 'AlShifaMySQL') { `$global:RebuildTestApi = 'Running'; return }
    `$global:RebuildTestPid = (Start-Process '$(Join-Path $mysqlHome 'bin\mysqld.exe')' -ArgumentList '--defaults-file=$ini' -WindowStyle Hidden -PassThru).Id
}
"@
[IO.File]::WriteAllText((Join-Path $scripts 'common.ps1'), (Get-Content (Join-Path $deploy 'common.ps1') -Raw) + $overrides)
[IO.File]::WriteAllText((Join-Path $scripts 'health-check.ps1'), "param(`$Root, [switch]`$Maintenance) 'OK     synthetic health'")
. (Join-Path $scripts 'common.ps1')
. (Join-Path $scripts 'db-integrity.ps1')

function Invoke-Sql([string]$Sql, [string]$Database = 'alshifa') { Invoke-Native (Join-Path $mysqlHome 'bin\mysql.exe') @("--defaults-extra-file=$rootCnf", '-N', '-B', '--default-character-set=utf8mb4', $Database, '-e', $Sql) }
function Wait-Up { foreach ($i in 1..90) { try { Invoke-Sql 'SELECT 1' 'mysql' | Out-Null; return } catch { Start-Sleep -Seconds 1 } }; throw 'scratch mysqld did not start' }
function Get-Fingerprint {
    $tables = @(Invoke-Sql "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA='alshifa' ORDER BY TABLE_NAME")
    ($tables | ForEach-Object { "$_=" + (Invoke-Sql "CHECKSUM TABLE ``$_`` EXTENDED" | ForEach-Object { ($_ -split "`t")[1] }) }) -join ';'
}

try {
    '== Scratch instance with synthetic rows'
    $bootstrap = Join-Path $work 'bootstrap.sql'
    Get-MySqlBootstrapSql 'alshifa' $secrets.Root $secrets.App $secrets.Backup $secrets.Restore | Set-Content $bootstrap -Encoding Ascii
    Invoke-Native (Join-Path $mysqlHome 'bin\mysqld.exe') @("--defaults-file=$ini", '--initialize-insecure', "--init-file=$bootstrap") | Out-Null
    Start-Service AlShifaMySQL; Wait-Up
    $ddl = @('CREATE TABLE __EFMigrationsHistory (MigrationId varchar(150) PRIMARY KEY, ProductVersion varchar(32) NOT NULL)', "INSERT INTO __EFMigrationsHistory VALUES ('20261124084651_OpticalOrderSettlementAndStockAudit','10.0.0')")
    foreach ($t in 'Patients', 'Products', 'StockBatches', 'StockMovements', 'Payments', 'AspNetUsers', 'IdempotencyRecords', 'SyncStates', 'SyncChanges') {
        $ddl += "CREATE TABLE $t (Id bigint AUTO_INCREMENT PRIMARY KEY, Name varchar(200) NULL, Amount decimal(18,2) NOT NULL, At datetime(6) NOT NULL, Body text NULL, Raw varbinary(64) NULL, Ratio double NULL)"
        $ddl += "INSERT INTO $t (Name, Amount, At, Body, Raw, Ratio) WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i<700) SELECT IF(i%7=0,NULL,CONCAT('مریض ',i,' ''quote'' \\ slash')), i*1.25, TIMESTAMP('2026-11-25 10:00:00.123456') - INTERVAL i MINUTE, REPEAT('x',i), UNHEX(SHA2(i,256)), i/3 FROM n"
    }
    foreach ($i in 1..45) { $ddl += "CREATE TABLE Extra$i (Id int PRIMARY KEY, V varchar(20)); INSERT INTO Extra$i VALUES ($i,'v$i')" }
    # Through a UTF-8 file: a command-line argument would lose the Persian text on Windows.
    $seed = Join-Path $work 'seed.sql'
    [IO.File]::WriteAllText($seed, (($ddl -join ";`n") + ';'), (New-Object Text.UTF8Encoding $false))
    Invoke-Sql "source $($seed -replace '\\', '/')" | Out-Null
    if ((Invoke-Sql "SELECT COUNT(*) FROM Patients WHERE Name LIKE '%مریض%'") -eq '0') { Write-Warning 'Persian text check skipped by console encoding' }
    $before = Get-Fingerprint
    # The kind of entries the hospital server logged on 2026-11-25.
    $line = "$((Get-Date).ToUniversalTime().AddSeconds(1).ToString('yyyy-MM-ddTHH:mm:ss.ffffff'))Z 0 [ERROR] [MY-013183] [InnoDB] Assertion failure: fsp0fsp.cc:3694:XDES_FREE_BIT thread 1`r`n"
    $stream = [IO.File]::Open((Join-Path $root 'logs\mysql.err'), 'Append', 'Write', 'ReadWrite')
    try { $bytes = [Text.Encoding]::ASCII.GetBytes($line); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
    Start-Sleep -Seconds 2
    if (-not @(Test-AlShifaDatabaseIntegrity $root).Count) { throw 'FAIL: logged damage was not detected' }
    'PASS: damage in the MySQL log detected'

    '== Failed import: original directory must come back'
    $bad = Join-Path $work 'bad.sql'
    [IO.File]::WriteAllText($bad, "THIS IS NOT SQL;`n-- Dump completed on 2026-11-25`n")
    $marker = Get-ChildItem (Join-Path $root 'data') -Filter 'auto.cnf' | ForEach-Object { (Get-FileHash $_.FullName).Hash }
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& { . '$(Join-Path $scripts 'common.ps1')'; `$global:RebuildTestPid = $($global:RebuildTestPid); & '$(Join-Path $scripts 'rebuild-database.ps1')' -Root '$root' -SqlFile '$bad'; exit `$LASTEXITCODE }" | Out-Host
    if ($LASTEXITCODE -eq 0) { throw 'FAIL: a broken import was reported as success' }
    $global:RebuildTestPid = (Get-Process mysqld | Where-Object { $_.Path -like "$mysqlHome*" -and $_.CommandLine -like "*$ini*" } | Select-Object -First 1).Id
    if (-not $global:RebuildTestPid) { $global:RebuildTestPid = (Get-CimInstance Win32_Process -Filter "Name='mysqld.exe'" | Where-Object { $_.CommandLine -like "*$ini*" } | Select-Object -First 1).ProcessId }
    Wait-Up
    if ((Get-FileHash (Join-Path $root 'data\auto.cnf')).Hash -ne $marker -or (Get-Fingerprint) -ne $before) { throw 'FAIL: original data directory not restored exactly' }
    if (-not (Get-ChildItem $root -Directory -Filter 'data.failed-*')) { throw 'FAIL: failed directory not kept' }
    'PASS: failed rebuild put the original data directory back, rows unchanged'

    '== Rebuild from the running server'
    Start-Sleep -Seconds 1
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& { . '$(Join-Path $scripts 'common.ps1')'; `$global:RebuildTestPid = $($global:RebuildTestPid); & '$(Join-Path $scripts 'rebuild-database.ps1')' -Root '$root'; exit `$LASTEXITCODE }" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'FAIL: rebuild did not succeed' }
    $global:RebuildTestPid = (Get-CimInstance Win32_Process -Filter "Name='mysqld.exe'" | Where-Object { $_.CommandLine -like "*$ini*" } | Select-Object -First 1).ProcessId
    Wait-Up
    if ((Get-Fingerprint) -ne $before) { throw 'FAIL: rows differ after rebuild' }
    $damagedDirs = @(Get-ChildItem $root -Directory -Filter 'data.damaged-*')
    if ($damagedDirs.Count -lt 1) { throw 'FAIL: damaged directory was not kept' }
    if (@(Test-AlShifaDatabaseIntegrity $root -Deep).Count) { throw 'FAIL: rebuilt database still reported as damaged' }
    if ((Invoke-Sql 'SELECT @@innodb_lock_wait_timeout' 'mysql') -ne '20') { throw 'FAIL: lock wait timeout not persisted' }
    'PASS: rebuild kept every row (CHECKSUM TABLE identical on all tables), kept the old directory, deep check clean'
    'RESULT: database rebuild verified'
} finally {
    try { Stop-Service AlShifaMySQL } catch { }
    Get-CimInstance Win32_Process -Filter "Name='mysqld.exe'" | Where-Object { $_.CommandLine -like "*$work*" } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    cmd /c rmdir "$(Join-Path $root 'mysql\bin')" | Out-Null
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
