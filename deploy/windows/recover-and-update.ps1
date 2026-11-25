# One step for a hospital server whose MySQL data directory was damaged during an interrupted update:
#   1. open the verified pre-update backup (it holds every record: the application was stopped when it was taken)
#   2. rebuild the database from it in a fresh data directory (the damaged one is kept, never deleted)
#   3. install the new version from the installer beside this folder, without another backup: the database is exactly that backup
#   4. check health and open the system. Any failure in step 3 puts back that backup and the previous version.
#Requires -RunAsAdministrator
param([string]$Root = 'C:\AlShifa', [string]$Backup)

. (Join-Path $PSScriptRoot 'common.ps1')
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')

if (-not $Backup) {
    $status = Get-Content -LiteralPath (Join-Path $Root 'logs\backup-status.json') -Raw | ConvertFrom-Json
    $Backup = Join-Path $status.destination $status.restoreVerifiedFile
}
if (-not (Test-Path -LiteralPath $Backup)) { throw "Backup $Backup not found. Nothing was changed." }
"== 1. Open the backup $Backup"
$open = Join-Path $Root ('backup-staging\recover-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'))
Expand-Backup (Find-Gpg) $Backup $open (Join-Path $Root 'secrets\backup-password.txt')
$dump = Join-Path $open 'db.sql'
if ((Get-Content -LiteralPath $dump -Tail 1) -notmatch '^-- Dump completed') { throw 'The backup dump is incomplete. Nothing was changed.' }
'Backup opened and complete.'
try {
    '== 2. Rebuild the database from the backup'
    & (Join-Path $PSScriptRoot 'rebuild-database.ps1') -Root $Root -SqlFile $dump -KeepStopped
    if ($LASTEXITCODE) { throw 'The rebuild failed; the original data directory was put back. Send this output to support.' }

    '== 3. Install the new version'
    $runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    $keys = [string](Get-Content -LiteralPath (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json).DataProtection.KeysPath
    [IO.File]::WriteAllText((Join-Path $keys 'update-maintenance'), $runId)
    & (Join-Path $PSScriptRoot 'finish-interrupted-update.ps1') -Root $Root -Apply -VerifiedBackup $Backup
    'ALL DONE: database rebuilt from the backup, new version installed, system open.'
} finally {
    # Patient data in the clear: removed whatever happened. The encrypted backup stays where it is.
    Remove-Item -LiteralPath $open -Recurse -Force -ErrorAction SilentlyContinue
}
