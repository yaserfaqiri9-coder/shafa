# Rebuilds the MySQL data directory of the hospital host from its own data, for a database whose files are damaged
# (InnoDB assertion failures, unreadable pages, a server left in innodb_force_recovery). Run elevated, outside working hours.
#
# Order: stop the application, export every table from the running server (or take -SqlFile, an export made earlier),
# count every table, stop MySQL, move the damaged data directory aside (never deleted), create a fresh one with the
# same accounts and passwords, import, prove that every table has the same number of rows and the protected business
# tables the same content, deep-check it, then start the application. Any failure after the move puts the old directory back.
#Requires -RunAsAdministrator
param(
    [string]$Root = 'C:\AlShifa',
    # An export made earlier with mysqldump, for a server that can no longer export by itself. Rows written after it are not in it.
    [string]$SqlFile,
    # Leave the application stopped and the maintenance lock in place: an update follows immediately.
    [switch]$KeepStopped
)

. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'db-integrity.ps1')
. (Join-Path $PSScriptRoot 'upgrade-safety.ps1')

$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$ini = Join-Path $Root 'mysql\alshifa.ini'
$bin = Join-Path $Root 'mysql\bin'
$dataDir = Join-Path $Root 'data'
$damaged = Join-Path $Root "data.damaged-$stamp"
$failedDir = Join-Path $Root "data.failed-$stamp"
$work = Join-Path $Root "backup-staging\rebuild-$stamp"
$secrets = Join-Path $Root 'secrets'
$rootCnf = Join-Path $secrets "rebuild-$stamp.cnf"
$backupCnf = Join-Path $secrets 'backup.cnf'
$logFile = Join-Path $Root "logs\rebuild-database-$stamp.log"
New-Item -ItemType Directory -Force $work, (Split-Path $logFile) | Out-Null
Start-Transcript -Path $logFile | Out-Null

function Read-CnfValue([string]$File, [string]$Name) {
    $line = Get-Content -LiteralPath $File | Where-Object { $_ -match "^$Name=" } | Select-Object -First 1
    if (-not $line) { throw "$Name missing in $File." }
    return ($line -split '=', 2)[1].Trim()
}

function Wait-MySql {
    foreach ($attempt in 1..90) {
        try { Invoke-Native (Join-Path $bin 'mysql.exe') @("--defaults-extra-file=$backupCnf", '-e', 'SELECT 1') | Out-Null; return } catch { Start-Sleep -Seconds 2 }
    }
    throw 'MySQL did not start within 3 minutes.'
}

function Get-TableCounts([string]$Cnf) {
    $mysql = Join-Path $bin 'mysql.exe'
    $tables = @(Invoke-Native $mysql @("--defaults-extra-file=$Cnf", '-N', '-B', '-e', "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA='alshifa' AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME"))
    $counts = [ordered]@{}
    foreach ($table in $tables) {
        if ($table -notmatch '^[A-Za-z0-9_]+$') { throw 'Unexpected table identifier.' }
        $counts[$table] = [long](@(Invoke-Native $mysql @("--defaults-extra-file=$Cnf", '-N', '-B', '-e', "SELECT COUNT(*) FROM ``alshifa``.``$table``"))[-1])
    }
    return $counts
}

$maintenanceFile = $null
$moved = $false
$succeeded = $false
$iniBackup = $null
try {
    if (-not (Test-Path -LiteralPath $ini)) { throw "$ini is missing. Is this the hospital server?" }
    if ([IO.Path]::GetFullPath(((Get-MySqlIniValue $Root 'datadir') -replace '/', '\')).TrimEnd('\') -ne $dataDir) { throw 'The MySQL data directory is not the standard one; nothing was changed.' }
    foreach ($name in 'AlShifaMySQL', 'AlShifaApi') { if (-not (Get-Service $name -ErrorAction SilentlyContinue)) { throw "Service $name is not installed; nothing was changed." } }
    $config = Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json
    $connection = New-Object System.Data.Common.DbConnectionStringBuilder
    $connection.set_ConnectionString([string]$config.ConnectionStrings.Default)
    $passwords = @{
        Root = (Get-Content (Join-Path $secrets 'mysql-root.txt') -Raw).Trim()
        App = [string]$connection['Password']
        Backup = Read-CnfValue $backupCnf 'password'
        Restore = Read-CnfValue (Join-Path $secrets 'restore.cnf') 'password'
    }
    if (-not $passwords.App) { throw 'The application database password was not found; nothing was changed.' }
    $port = [int](Get-MySqlIniValue $Root 'port')
    $free = (Get-PSDrive ([IO.Path]::GetPathRoot($Root).Substring(0, 1))).Free
    $size = (Get-ChildItem -LiteralPath $dataDir -Recurse -File -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum
    if ($free -lt [Math]::Max(3GB, 2 * $size)) { throw "Not enough free disk space for a rebuild ($([Math]::Round($free / 1GB, 1)) GB free); nothing was changed." }

    '== Stop the application'
    $maintenanceFile = Join-Path ([string]$config.DataProtection.KeysPath) 'update-maintenance'
    [IO.File]::WriteAllText($maintenanceFile, "rebuild-$stamp")
    Stop-Service AlShifaApi

    '== Export the current data'
    Write-ClientCnf $rootCnf 'root' $passwords.Root $port
    $dump = Join-Path $work 'db.sql'
    $sourceCounts = $null
    $sourceProof = $null
    if ($SqlFile) {
        if (-not (Test-Path -LiteralPath $SqlFile)) { throw "$SqlFile was not found; nothing was changed." }
        if ((Get-Content -LiteralPath $SqlFile -Tail 1) -notmatch '^-- Dump completed') { throw 'The SQL file is incomplete (no "-- Dump completed" line); nothing was changed.' }
        Copy-Item -LiteralPath $SqlFile -Destination $dump
        "Using the export $SqlFile. Rows written after it was made are not in it."
    } else {
        try {
            Invoke-Native (Join-Path $bin 'mysqldump.exe') @("--defaults-extra-file=$rootCnf", '--single-transaction', '--quick', '--no-tablespaces', '--set-gtid-purged=OFF',
                '--routines', '--events', '--triggers', '--hex-blob', '--default-character-set=utf8mb4', "--result-file=$dump", 'alshifa') | Out-Null
            if ((Get-Content -LiteralPath $dump -Tail 1) -notmatch '^-- Dump completed') { throw 'the export is incomplete' }
            $sourceCounts = Get-TableCounts $rootCnf
            $sourceProof = Get-UpgradeDataProof (Join-Path $bin 'mysql.exe') $backupCnf 'alshifa' -ReadOnlySource
        } catch {
            throw "The current database could not be read completely ($_). Nothing was changed. Run again with the SQL export: Rebuild-Database.bat <file.sql>"
        }
        "Exported $($sourceCounts.Count) tables, $(($sourceCounts.Values | Measure-Object -Sum).Sum) rows."
    }

    '== Move the damaged data directory aside'
    Stop-Service AlShifaMySQL
    (Get-Service AlShifaMySQL).WaitForStatus('Stopped', [TimeSpan]::FromMinutes(3))
    Move-Item -LiteralPath $dataDir -Destination $damaged
    $moved = $true
    "Kept unchanged at $damaged"
    if (Get-MySqlIniValue $Root 'innodb_force_recovery') {
        $iniBackup = "$ini.before-rebuild-$stamp"
        Copy-Item -LiteralPath $ini -Destination $iniBackup
        $lines = Get-Content -LiteralPath $ini | Where-Object { $_ -notmatch '^\s*innodb_force_recovery\s*=' }
        Set-Content -LiteralPath $ini -Value $lines -Encoding Ascii
        'Removed innodb_force_recovery from alshifa.ini'
    }

    '== Create a fresh data directory'
    $bootstrap = Join-Path $secrets "bootstrap-$stamp.sql"
    Get-MySqlBootstrapSql 'alshifa' $passwords.Root $passwords.App $passwords.Backup $passwords.Restore | Set-Content $bootstrap -Encoding Ascii
    try {
        Invoke-Native (Join-Path $bin 'mysqld.exe') @("--defaults-file=$ini", '--initialize-insecure', "--init-file=$bootstrap") | Out-Null
    } finally {
        Remove-Item -LiteralPath $bootstrap -Force -ErrorAction SilentlyContinue
    }
    Protect-Path $dataDir @('NT SERVICE\AlShifaMySQL=F')
    Start-Service AlShifaMySQL
    Wait-MySql

    '== Import'
    Invoke-Native (Join-Path $bin 'mysql.exe') @("--defaults-extra-file=$rootCnf", '--default-character-set=utf8mb4', 'alshifa', '-e', "source $($dump -replace '\\', '/')") | Out-Null
    Invoke-Native (Join-Path $bin 'mysql.exe') @("--defaults-extra-file=$rootCnf", '-e', 'SET PERSIST innodb_lock_wait_timeout=20;') | Out-Null

    '== Verify'
    $counts = Get-TableCounts $rootCnf
    if ($counts.Count -lt 50 -or -not $counts.Contains('__EFMigrationsHistory') -or $counts['__EFMigrationsHistory'] -lt 1) { throw "The rebuilt database looks incomplete: $($counts.Count) tables." }
    if ($sourceCounts) {
        $differences = @($sourceCounts.Keys | Where-Object { -not $counts.Contains($_) -or $counts[$_] -ne $sourceCounts[$_] })
        if ($differences.Count -or $counts.Count -ne $sourceCounts.Count) { throw "Row counts differ after the rebuild: $($differences -join ', ')" }
        $proof = Get-UpgradeDataProof (Join-Path $bin 'mysql.exe') $backupCnf 'alshifa' $sourceProof.layout -ReadOnlySource
        if (@(Compare-Object $sourceProof.values $proof.values).Count) { throw 'Protected business rows differ after the rebuild.' }
        "Every table has the same row count; patients, stock, invoices, payments, ledger, users and sync state are identical."
    }
    $integrity = @(Test-AlShifaDatabaseIntegrity $Root -Deep)
    if ($integrity.Count) { throw "The rebuilt database failed the integrity check: $($integrity -join ' | ')" }
    'Integrity: CHECK TABLE passed on every table.'

    if ($KeepStopped) {
        'Rebuild complete. The application stays stopped for the update that follows.'
        $maintenanceFile = $null
        $succeeded = $true
        Stop-Transcript | Out-Null
        $global:LASTEXITCODE = 0
        return
    }
    '== Start the application'
    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    $maintenanceFile = $null
    Start-Service AlShifaApi
    Start-Sleep -Seconds 5
    & (Join-Path $PSScriptRoot 'health-check.ps1') -Root $Root -Maintenance
    "Rebuild complete. The damaged data directory is kept at $damaged; remove it only after a few days of normal work and a verified backup."
    "Take a fresh backup now (Settings > Backup, or scripts\backup.ps1)."
    $succeeded = $true
} catch {
    $reason = $_
    "REBUILD FAILED: $reason"
    if ($moved) {
        '== Undo: putting the original data directory back'
        try {
            Stop-Service AlShifaMySQL -ErrorAction SilentlyContinue
            (Get-Service AlShifaMySQL).WaitForStatus('Stopped', [TimeSpan]::FromMinutes(3))
            if (Test-Path -LiteralPath $dataDir) { Move-Item -LiteralPath $dataDir -Destination $failedDir }
            Move-Item -LiteralPath $damaged -Destination $dataDir
            if ($iniBackup) { Copy-Item -LiteralPath $iniBackup -Destination $ini -Force }
            Start-Service AlShifaMySQL
            Wait-MySql
            'The original data directory is back in place, exactly as before.'
        } catch {
            "UNDO FAILED: $_. The original data directory is at $damaged; do not delete anything and call technical support."
        }
    }
    if ($maintenanceFile) { Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue }
    Start-Service AlShifaApi -ErrorAction SilentlyContinue
    Stop-Transcript | Out-Null
    exit 1
} finally {
    Remove-Item -LiteralPath $rootCnf -Force -ErrorAction SilentlyContinue
    # The export holds patient data in the clear. After a verified rebuild it is no longer needed; after a failure it is kept for support.
    if ($succeeded) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } else { "The export is kept at $work (patient data in the clear; protect it)." }
}
Stop-Transcript | Out-Null
