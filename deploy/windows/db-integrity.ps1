# Signs that the hospital database files are damaged. Dot-source after common.ps1. Read-only: changes nothing.
# Cheap checks (recovery mode, corruption entries in the MySQL error log) run nightly and in health-check.ps1.
# The deep check (CHECK TABLE on every table) runs only in a maintenance window, before an update or after a rebuild.

# InnoDB entries that mean a page could not be read or a structure is inconsistent. A clean crash recovery logs none of these.
$script:CorruptionPatterns = @(
    'Assertion failure',
    'Database page corruption',
    'Unable to read page',
    'is corrupted',
    'corrupt(ed)? page',
    'Page \[page id:.*\] log sequence number .* is in the future',
    '\[MY-011899\]', '\[MY-013183\]', '\[MY-011906\]', '\[MY-012153\]', '\[MY-011937\]', '\[MY-013061\]'
)

function Get-MySqlIniValue([string]$Root, [string]$Name) {
    $ini = Join-Path $Root 'mysql\alshifa.ini'
    if (-not (Test-Path -LiteralPath $ini)) { return $null }
    $line = Get-Content -LiteralPath $ini | Where-Object { $_ -match "^\s*$([regex]::Escape($Name))\s*=" } | Select-Object -Last 1
    if ($line) { return ($line -split '=', 2)[1].Trim() }
    return $null
}

function Get-MySqlErrorLogPath([string]$Root) {
    $configured = Get-MySqlIniValue $Root 'log_error'
    if ($configured) { return ($configured -replace '/', '\') }
    return (Join-Path $Root 'logs\mysql.err')
}

# The data directory is created by mysqld --initialize, which writes auto.cnf once. Entries older than that belong to a
# data directory that has since been rebuilt and say nothing about the current one.
function Get-DataDirectoryCreatedUtc([string]$Root) {
    $dataDir = Get-MySqlIniValue $Root 'datadir'
    if (-not $dataDir) { $dataDir = Join-Path $Root 'data' }
    $marker = Join-Path ($dataDir -replace '/', '\') 'auto.cnf'
    if (Test-Path -LiteralPath $marker) { return (Get-Item -LiteralPath $marker).CreationTimeUtc }
    return [DateTime]::MinValue
}

# Corruption entries written to the error log after $SinceUtc. MySQL writes UTC timestamps (log_timestamps=UTC, the default).
function Get-MySqlCorruptionEntries([string]$Root, [DateTime]$SinceUtc) {
    $log = Get-MySqlErrorLogPath $Root
    if (-not (Test-Path -LiteralPath $log)) { return @() }
    $pattern = [regex]('(' + ($script:CorruptionPatterns -join '|') + ')')
    $found = New-Object System.Collections.Generic.List[string]
    # mysqld keeps the file open; share it for reading and writing.
    $share = [IO.FileShare]([int][IO.FileShare]::ReadWrite -bor [int][IO.FileShare]::Delete)
    $stream = [IO.File]::Open($log, [IO.FileMode]::Open, [IO.FileAccess]::Read, $share)
    $reader = New-Object IO.StreamReader($stream)
    try {
        $lastStamp = [DateTime]::MinValue
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line -match '^(\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d)') {
                $lastStamp = [DateTime]::SpecifyKind([DateTime]::ParseExact($Matches[1], 'yyyy-MM-ddTHH:mm:ss', [Globalization.CultureInfo]::InvariantCulture), 'Utc')
            }
            # An assertion's own lines carry no timestamp; they count with the last stamped line before them.
            if ($lastStamp -gt $SinceUtc -and $pattern.IsMatch($line)) {
                $found.Add($(if ($line.Length -gt 300) { $line.Substring(0, 300) } else { $line }))
                if ($found.Count -ge 20) { break }
            }
        }
    } finally {
        $reader.Dispose()
    }
    return @($found)
}

function Get-MySqlRuntimeFacts([string]$MySql, [string]$Cnf) {
    $lines = @(Invoke-Native $MySql @("--defaults-extra-file=$Cnf", '-N', '-B', '-e', "SELECT @@innodb_force_recovery, @@global.read_only, @@innodb_read_only; SHOW GLOBAL STATUS LIKE 'Uptime';"))
    $flags = $lines[0] -split "`t"
    $uptime = [long](($lines | Where-Object { $_ -match '^Uptime\t' } | Select-Object -First 1) -split "`t")[1]
    return @{ ForceRecovery = [int]$flags[0]; ReadOnly = ($flags[1] -ne '0' -or $flags[2] -ne '0'); UptimeSeconds = $uptime }
}

# Returns a list of problems; empty means no sign of damage. -Deep also reads every table with CHECK TABLE, which blocks
# writes table by table: use it only while the application is stopped.
function Test-AlShifaDatabaseIntegrity([string]$Root, [string]$Database = 'alshifa', [switch]$Deep, [string]$MySqlBin) {
    $problems = New-Object System.Collections.Generic.List[string]
    # backup.ps1 may run against a MySQL outside the installation folder (-MySqlBin); the same client checks it.
    if (-not $MySqlBin) { $MySqlBin = Join-Path $Root 'mysql\bin' }
    $mysql = Join-Path $MySqlBin 'mysql.exe'
    $cnf = Join-Path $Root 'secrets\backup.cnf'
    $since = Get-DataDirectoryCreatedUtc $Root

    $iniRecovery = Get-MySqlIniValue $Root 'innodb_force_recovery'
    if ($iniRecovery -and $iniRecovery -ne '0') { $problems.Add("mysql\alshifa.ini still sets innodb_force_recovery=$iniRecovery (emergency recovery mode).") }

    $before = $null
    try { $before = Get-MySqlRuntimeFacts $mysql $cnf } catch { $problems.Add("The database server does not answer: $_"); return @($problems) }
    if ($before.ForceRecovery -ne 0) { $problems.Add("MySQL is running with innodb_force_recovery=$($before.ForceRecovery) (emergency recovery mode).") }
    if ($before.ReadOnly) { $problems.Add('MySQL is running read-only.') }

    foreach ($entry in (Get-MySqlCorruptionEntries $Root $since)) { $problems.Add("MySQL error log: $entry") }

    if ($Deep -and -not $problems.Count) {
        $started = Get-Date
        $tables = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', '-e', "SELECT TABLE_NAME FROM information_schema.TABLES WHERE TABLE_SCHEMA='$Database' AND TABLE_TYPE='BASE TABLE' ORDER BY TABLE_NAME"))
        foreach ($table in $tables) { if ($table -notmatch '^[A-Za-z0-9_]+$') { throw 'Unexpected table identifier.' } }
        if ($tables.Count) {
            $sql = ($tables | ForEach-Object { "CHECK TABLE ``$Database``.``$_``;" }) -join ' '
            try {
                $rows = @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', '-e', $sql))
                foreach ($row in $rows) {
                    $cells = $row -split "`t"
                    if ($cells.Count -lt 4) { continue }
                    if ($cells[2] -eq 'error' -or ($cells[2] -eq 'status' -and $cells[3] -notin @('OK', 'Table is already up to date')) -or ($cells[2] -eq 'warning' -and $cells[3] -match 'corrupt')) {
                        $problems.Add("CHECK TABLE $($cells[0]): $($cells[2]) $($cells[3])")
                    }
                }
            } catch {
                $problems.Add("CHECK TABLE could not finish: $_")
            }
        }
        # A server that crashed and restarted during the check has a shorter uptime than before it.
        try {
            $after = Get-MySqlRuntimeFacts $mysql $cnf
            if ($after.UptimeSeconds -lt $before.UptimeSeconds) { $problems.Add('The database server restarted during the integrity check.') }
        } catch { $problems.Add("The database server stopped answering during the integrity check: $_") }
        foreach ($entry in (Get-MySqlCorruptionEntries $Root ($started.ToUniversalTime().AddSeconds(-2)))) {
            if (-not $problems.Contains("MySQL error log: $entry")) { $problems.Add("MySQL error log: $entry") }
        }
    }
    return @($problems)
}
