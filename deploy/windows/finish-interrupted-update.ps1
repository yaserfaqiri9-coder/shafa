# Finishes an update that was cut off (power loss, Windows Update restart) after update.ps1 had stopped the application.
# The maintenance lock has kept every user out since then, so the verified backup that run took still holds all data,
# and the update is completed forward without a new backup. Without -Apply it only reports what it finds.
#   A  cut off before the files were replaced   -> install the new program, migrate, check, open
#   B  files replaced and database migrated      -> restore the Sync settings, check, open
#   C  files replaced (perhaps partly)           -> install the new program again, migrate, check, open
#   D  migration only partly applied             -> restore the verified pre-update backup, put the previous program back
# Any failure while finishing restores the verified pre-update backup and the previous program. Nothing is deleted:
# replaced program folders are kept as app.failed-<run>. Stops for review on anything unexpected.
#Requires -RunAsAdministrator
param(
    [string]$Root = 'C:\AlShifa',
    [switch]$Apply,
    [string]$NewMigration = '20261125105153_OpticalOrderCustomerName',
    [string]$PreviousMigration = '20261124084651_OpticalOrderSettlementAndStockAudit',
    # The unpacked update. Found on its own: the installer's work folder, or Install-AlShifa.exe / Setup.exe beside this folder.
    [string]$Package,
    # A backup known to hold exactly the current data (the database was just rebuilt from it with users kept out).
    [string]$VerifiedBackup
)

. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'db-integrity.ps1')
. (Join-Path $PSScriptRoot 'sync-upgrade-common.ps1')

$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$appDir = Join-Path $Root 'app'
$configFile = Join-Path $appDir 'appsettings.Production.json'
$lock = Join-Path $Root 'keys\update-maintenance'
$mysql = Join-Path $Root 'mysql\bin\mysql.exe'
$cnf = Join-Path $Root 'secrets\backup.cnf'

if (-not (Get-Command Invoke-AlShifaSql -ErrorAction SilentlyContinue)) {
    function Invoke-AlShifaSql([string]$Sql) { @(Invoke-Native $mysql @("--defaults-extra-file=$cnf", '-N', '-B', 'alshifa', '-e', $Sql)) }
}
if (-not (Get-Command Test-AlShifaHealthy -ErrorAction SilentlyContinue)) {
    function Test-AlShifaHealthy {
        $url = (Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json).Kestrel.Endpoints.Loopback.Url
        foreach ($attempt in 1..30) {
            try { if ((Invoke-WebRequest "$url/health" -UseBasicParsing -TimeoutSec 10).Content -eq 'Healthy') { return $true } } catch { }
            Start-Sleep -Seconds 3
        }
        return $false
    }
}

function Wait-Database {
    if ((Get-Service AlShifaMySQL).Status -ne 'Running') { Start-Service AlShifaMySQL }
    foreach ($attempt in 1..60) { try { Invoke-AlShifaSql 'SELECT 1' | Out-Null; return } catch { Start-Sleep -Seconds 2 } }
    throw 'The database did not start. Nothing was changed.'
}

function Restore-PreviousProgram([string]$Previous, [string]$RunId) {
    Stop-Service AlShifaApi -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $appDir) { Move-Item -LiteralPath $appDir -Destination (Join-Path $Root "app.failed-$RunId") }
    Move-Item -LiteralPath $Previous -Destination $appDir
    'The previous program is back in place; the replaced one is kept as app.failed-' + $RunId
}

# Health is checked while the lock still keeps users out (only /health answers), so nothing can be entered into a version
# that might yet be undone. The application reads the lock on every request: removing it opens the system at once.
function Start-Unlocked {
    Restart-Service AlShifaApi
    if (-not (Test-AlShifaHealthy)) { throw 'The application did not report Healthy after start. Users are still kept out; send this output to support.' }
    Remove-Item -LiteralPath $lock -Force
    'Application healthy and open for users.'
}

'== State'
Get-Service AlShifaMySQL, AlShifaApi | ForEach-Object { "Service $($_.Name): $($_.Status)" }
if (-not (Test-Path -LiteralPath $lock)) { 'No interrupted update: the maintenance lock is not present. Nothing to do.'; exit 0 }
$runId = (Get-Content -LiteralPath $lock -Raw).Trim()
if ($runId -notmatch '^\d{8}T\d{6}Z-[0-9a-f]{8}$') { throw "The maintenance lock ($runId) was not written by an update (a rebuild uses its own). Stop for review." }
"Interrupted update run: $runId"
$previous = Join-Path $Root "app.previous-$runId"
$hasPrevious = Test-Path -LiteralPath $previous
"Previous program kept aside: $hasPrevious"

Wait-Database
$damage = @(Test-AlShifaDatabaseIntegrity $Root)
if ($damage.Count) { $damage | ForEach-Object { "DAMAGE: $_" }; throw 'The database shows signs of damage. Do not continue; run Rebuild-Database.bat first.' }
'Database integrity: OK'
$last = @(Invoke-AlShifaSql 'SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1')[-1]
$added = [int]@(Invoke-AlShifaSql "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='OpticalOrders' AND COLUMN_NAME IN ('CustomerName','CustomerPhone')")[-1]
"Database migration: $last (new columns present: $added of 2)"

$case = if (-not $hasPrevious -and $last -eq $PreviousMigration -and $added -eq 0) { 'A' }
    elseif ($hasPrevious -and $last -eq $NewMigration -and $added -eq 2) { 'B' }
    elseif ($hasPrevious -and $last -eq $PreviousMigration -and $added -eq 0) { 'C' }
    elseif ($hasPrevious -and $last -eq $PreviousMigration) { 'D' }
    else { $null }
if (-not $case) { throw 'This combination is not one the tool knows. Nothing was changed; send this output to support.' }

# The backup of this very run, taken after the lock was set: nothing can have been entered since.
$status = Get-Content -LiteralPath (Join-Path $Root 'logs\backup-status.json') -Raw | ConvertFrom-Json
$backup = if ($status.restoreVerifiedFile) { Join-Path $status.destination $status.restoreVerifiedFile } else { $null }
$backupFresh = $backup -and (Test-Path -LiteralPath $backup) -and $status.restoreVerifiedAt -and
    ([DateTime]::Parse([string]$status.restoreVerifiedAt).ToUniversalTime() -gt (Get-Item -LiteralPath $lock).LastWriteTimeUtc)
if ($VerifiedBackup -and (Test-Path -LiteralPath $VerifiedBackup)) { $backup = $VerifiedBackup; $backupFresh = $true }
"Verified backup of this update: $(if ($backupFresh) { $backup } else { 'NOT FOUND' })"
if (-not $backupFresh) { throw 'The verified backup of this update was not found, so finishing is not safe. Nothing was changed; send this output to support.' }

function Find-Package {
    $isPackage = { param($d) $d -and (Test-Path -LiteralPath (Join-Path $d 'app\AlShifa.Api.exe')) -and (Test-Path -LiteralPath (Join-Path $d 'scripts\update.ps1')) }
    if (& $isPackage $Package) { return $Package }
    # The installer beside this folder first: it is certainly the new version. The installer's own work folder only as a fallback.
    $installer = @('Install-AlShifa.exe', 'Setup.exe') | ForEach-Object { Join-Path (Split-Path $PSScriptRoot -Parent) $_ } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $installer) {
        $workDir = Join-Path $env:ProgramFiles 'AlShifaSetup\work'
        if (& $isPackage $workDir) { return $workDir }
        return $null
    }
    # Same layout as InstallAlShifa.cs: zip, its length, its SHA-256 and the mark ALSHIFA1 at the end of the program.
    $target = Join-Path $Root "backup-staging\package-$runId"
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Force $target | Out-Null
    $zip = "$target.zip"
    $exe = [IO.File]::OpenRead($installer)
    try {
        $tail = New-Object byte[] 48
        [void]$exe.Seek(-48, 'End'); [void]$exe.Read($tail, 0, 48)
        if ([Text.Encoding]::ASCII.GetString($tail, 40, 8) -ne 'ALSHIFA1') { throw "$installer is not a complete AlShifa installer." }
        $length = [BitConverter]::ToInt64($tail, 0)
        [void]$exe.Seek(-48 - $length, 'End')
        $out = [IO.File]::Create($zip)
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $buffer = New-Object byte[] 1048576; $left = $length
            while ($left -gt 0) {
                $read = $exe.Read($buffer, 0, [int][Math]::Min($buffer.Length, $left))
                if ($read -le 0) { throw 'The installer file is incomplete.' }
                [void]$sha.TransformBlock($buffer, 0, $read, $null, 0); $out.Write($buffer, 0, $read); $left -= $read
            }
            [void]$sha.TransformFinalBlock($buffer, 0, 0)
            if ([Convert]::ToBase64String($sha.Hash) -ne [Convert]::ToBase64String($tail, 8, 32)) { throw 'The installer file is damaged.' }
        } finally { $out.Dispose(); $sha.Dispose() }
    } finally { $exe.Dispose() }
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $target)
    Remove-Item -LiteralPath $zip -Force
    return $target
}

$pkg = $null
if ($case -in 'A', 'C') {
    $pkg = Find-Package
    if (-not $pkg) { throw 'The update package was not found. Put this folder inside the installer folder (beside Install-AlShifa.exe) and run it again. Nothing was changed.' }
    "Update package: $pkg"
}
$plan = @{
    A = 'Cut off before the program files were replaced. Plan: install the new program, migrate, check, open. No new backup needed.'
    B = 'Program replaced and database migrated; only the last steps were missing. Plan: restore the Sync settings, check, open.'
    C = 'Cut off while replacing the program files; database untouched. Plan: install the new program again, migrate, check, open. No new backup needed.'
    D = 'Database migration only partly applied. Plan: restore the verified pre-update backup and the previous program; run the installer again afterwards.'
}[$case]
"RESULT: case $case. $plan"
if (-not $Apply) { 'Report only: nothing was changed. Run again with -Apply to carry out the plan.'; exit 0 }

function Undo-Update([string]$Reason) {
    "FAILED: $Reason"
    '== Undo: pre-update database and previous program'
    Stop-Service AlShifaApi -ErrorAction SilentlyContinue
    Restore-AlShifaDatabase $Root $backup
    if (Test-Path -LiteralPath $previous) { Restore-PreviousProgram $previous $runId }
    Start-Unlocked
    throw "The update could not be finished; the hospital runs the previous version on the pre-update database ($backup). Reason: $Reason"
}

function Install-NewProgram {
    Stop-Service AlShifaApi -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $previous) {
        # The partial copy is kept for review, never deleted.
        Move-Item -LiteralPath $appDir -Destination (Join-Path $Root "app.failed-$runId")
    } else {
        Move-Item -LiteralPath $appDir -Destination $previous
    }
    New-Item -ItemType Directory $appDir | Out-Null
    Copy-Item (Join-Path $pkg 'app\*') $appDir -Recurse
    Copy-Item (Join-Path $previous 'appsettings.Production.json') $appDir
    # The same settings update.ps1 writes: status files, and Sync held until health has passed.
    $updated = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    if (-not $updated.PSObject.Properties['Update']) { $updated | Add-Member -NotePropertyName Update -NotePropertyValue ([pscustomobject]@{}) }
    $updated.Update | Add-Member -NotePropertyName StatusFile -NotePropertyValue (Join-Path $Root 'logs\update-status.json') -Force
    $updated.Update | Add-Member -NotePropertyName ApprovalFile -NotePropertyValue (Join-Path $Root 'requests\update-approval.json') -Force
    if ($snapshot) { $updated.Sync | Add-Member -NotePropertyName RequireCompatibilityApproval -NotePropertyValue $true -Force }
    [IO.File]::WriteAllText($configFile, ($updated | ConvertTo-Json -Depth 32))
    Protect-Path $appDir @('NT SERVICE\AlShifaApi=RX')
    Copy-Item (Join-Path $pkg 'scripts\*') (Join-Path $Root 'scripts') -Recurse -Force
    foreach ($old in 'director-access.ps1', 'remote-access.ps1') { Remove-Item (Join-Path $Root "scripts\$old") -Force -ErrorAction SilentlyContinue }
    try { Add-BackupRequestTrigger; Set-NightlyBackupSchedule; Set-AutoUpdateTasks $Root } catch { "WARNING: scheduled tasks kept as they were: $_" }
    'New program files in place.'
}

$previousConfig = Get-Content -LiteralPath $(if (Test-Path -LiteralPath $previous) { Join-Path $previous 'appsettings.Production.json' } else { $configFile }) -Raw | ConvertFrom-Json
$snapshot = Get-EstablishedSyncUpgradeSnapshot $previousConfig $Root

'== Apply'
if ($case -eq 'D') {
    Stop-Service AlShifaApi -ErrorAction SilentlyContinue
    "Restoring $backup"
    Restore-AlShifaDatabase $Root $backup
    Restore-PreviousProgram $previous $runId
    Start-Unlocked
    'Done. Run the installer again when convenient.'
    exit 0
}
if ($case -in 'A', 'C') {
    try { Install-NewProgram } catch { Undo-Update "copying the new program: $_" }
    '== Migrate'
    Push-Location $appDir
    try { Invoke-Native (Join-Path $appDir 'AlShifa.Api.exe') @('--migrate', '--backup-taken') | Out-Null; 'Migration applied.' }
    catch { Pop-Location; Undo-Update "migration: $_" }
    Pop-Location
}
try {
    $current = Get-Content -LiteralPath $configFile -Raw | ConvertFrom-Json
    if ($snapshot -and $current.Sync.RequireCompatibilityApproval) {
        Restore-EstablishedSyncUpgradeConfiguration $snapshot $configFile
        'Sync settings restored exactly as before the update (identity, key and checkpoints unchanged).'
    }
    Start-Unlocked
} catch {
    Undo-Update "health: $_"
}
if ($snapshot) { Write-EstablishedSyncUpgradeStatus $Root }
"Update finished. Previous program kept at $previous."
