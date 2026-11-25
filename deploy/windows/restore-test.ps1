# Restores a backup into a throwaway database, checks it, and removes it. Production is never touched:
# the name must start with alshifa_restore_, and the account in restore.cnf can reach only databases named that way.
param(
    # A backup file, or a folder whose newest backup is used.
    [Parameter(Mandatory)][string]$Backup,
    [string]$Root = 'C:\AlShifa',
    [string]$MySqlBin,
    [string]$Gpg,
    [string]$TargetDatabase = ('alshifa_restore_' + [Guid]::NewGuid().ToString('N')),
    [string]$CandidateApp,
    [hashtable]$ExpectedSourceProof
)

. (Join-Path $PSScriptRoot 'common.ps1')

if ($TargetDatabase -notmatch '^alshifa_restore_[a-z0-9_]+$') { throw 'The target database must be named alshifa_restore_<name>.' }
if (-not $MySqlBin) { $MySqlBin = Join-Path $Root 'mysql\bin' }

if (Test-Path $Backup -PathType Container) {
    $file = Get-ChildItem $Backup -Filter 'alshifa-*.zip.gpg' | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $file) { throw "No backups found in $Backup" }
    $Backup = $file.FullName
}

$work =Join-Path $Root "backup-staging\restore-$([Guid]::NewGuid().ToString('N'))"
$mysql = Join-Path $MySqlBin 'mysql.exe'
$credentials = "--defaults-extra-file=$(Join-Path $Root 'secrets\restore.cnf')"
$createdDatabase = $false

function Invoke-Query([string]$Sql) {
    Invoke-Native $mysql @($credentials, '-N', '-B', $TargetDatabase, '-e', $Sql)
}

try {
    "Backup: $Backup"
    Expand-Backup (Find-Gpg $Gpg) $Backup $work (Join-Path $Root 'secrets\backup-password.txt')
    "Integrity check and decryption: OK"

    $manifestPath = Join-Path $work 'manifest.json'
    if (Test-Path -LiteralPath $manifestPath) {
        foreach ($entry in (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json)) {
            $filePath = [IO.Path]::GetFullPath((Join-Path $work $entry.path))
            if (-not $filePath.StartsWith([IO.Path]::GetFullPath($work).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid backup manifest path.' }
            if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) { throw 'A manifest file is missing.' }
            if ((Get-Item -LiteralPath $filePath).Length -ne $entry.sizeBytes -or (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash -ne $entry.sha256) { throw 'Backup manifest verification failed.' }
        }
    }

    # CREATE fails if an operator supplied an existing database; never empty it.
    Invoke-Native $mysql @($credentials, '-e', "CREATE DATABASE $TargetDatabase CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;") | Out-Null
    $createdDatabase = $true
    Invoke-Native $mysql @($credentials, $TargetDatabase, '-e', "source $((Join-Path $work 'db.sql') -replace '\\', '/')") | Out-Null

    $tables = [int](Invoke-Query "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE()")
    $migrations = [int](Invoke-Query 'SELECT COUNT(*) FROM __EFMigrationsHistory')
    if ($tables -lt 50 -or $migrations -lt 1) { throw "Restored database looks incomplete: $tables tables, $migrations migrations." }

    $unbalanced = [int](Invoke-Query 'SELECT COUNT(*) FROM (SELECT TransactionId FROM LedgerEntries GROUP BY TransactionId HAVING SUM(Debit) <> SUM(Credit)) t')
    if ($unbalanced -ne 0) { throw "$unbalanced ledger transactions are unbalanced." }

    # Every recorded attachment must exist and match the hash stored when it was uploaded.
    $attachments = @(Invoke-Query 'SELECT StoredName, Sha256 FROM Attachments')
    $sha = [Security.Cryptography.SHA256]::Create()
    foreach ($row in $attachments) {
        $name, $expected = $row -split "`t"
        $path = Join-Path $work "storage\$name"
        if (-not (Test-Path $path)) { throw "Attachment file is missing from the backup: $name" }
        $bytes = [IO.File]::ReadAllBytes($path)
        if ([BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '') -ne $expected) { throw "Attachment does not match its hash: $name" }
    }

    if($CandidateApp){
        . (Join-Path $PSScriptRoot 'upgrade-safety.ps1')
        if($ExpectedSourceProof){
            $restored=Get-UpgradeDataProof $mysql (Join-Path $Root 'secrets\restore.cnf') $TargetDatabase $ExpectedSourceProof.layout
            if(@(Compare-Object $ExpectedSourceProof.values $restored.values).Count){throw 'Restored protected rows differ from the stopped source database; upgrade refused.'}
        }
        Test-UpgradeCandidate $mysql (Join-Path $Root 'secrets\restore.cnf') $TargetDatabase $CandidateApp $work
        if([int](Invoke-Query 'SELECT COUNT(*) FROM (SELECT TransactionId FROM LedgerEntries GROUP BY TransactionId HAVING SUM(Debit) <> SUM(Credit)) t') -ne 0){throw 'Candidate ledger verification failed.'}
    }
    "Tables verified: $tables; attachment hashes and ledger balance verified."
    'RESULT: backup restored and verified in an isolated database'
} finally {
    if ($createdDatabase) { Invoke-Native $mysql @($credentials, '-e', "DROP DATABASE $TargetDatabase;") | Out-Null }
    $resolvedWork = [IO.Path]::GetFullPath($work)
    if (-not $resolvedWork.StartsWith([IO.Path]::GetFullPath((Join-Path $Root 'backup-staging')).TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid restore staging path.' }
    Remove-Item -LiteralPath $resolvedWork -Recurse -Force -ErrorAction SilentlyContinue
}
