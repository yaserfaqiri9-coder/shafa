# Installs a new release on the hospital host, outside working hours. Run elevated, from the scripts folder of the new release.
# Order: stop, fresh backup, prove the backup restores, keep the old version, copy, migrate, start, check. Stops at the first problem.
#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory)][string]$BackupDestination,
    [string]$Root = 'C:\AlShifa',
    [switch]$AllowSameDisk,
    [switch]$HoldOnlineServices
)

. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'runtime-common.ps1')
. (Join-Path $PSScriptRoot 'upgrade-safety.ps1')
. (Join-Path $PSScriptRoot 'sync-upgrade-common.ps1')
. (Join-Path $PSScriptRoot 'db-integrity.ps1')

$release = Split-Path $PSScriptRoot -Parent
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$appDir = Join-Path $Root 'app'
$runId = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$previous = Join-Path $Root ("app.previous-" + $runId)
$failed = Join-Path $Root ("app.failed-" + $runId)
$config = Join-Path $appDir 'appsettings.Production.json'
if (-not (Test-Path $config)) { throw "$config is missing. Is this the right computer?" }
$hostConfig = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
$installedService = Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
if (-not $installedService -or $installedService.PathName.Trim().Trim('"') -ne (Join-Path $appDir 'AlShifa.Api.exe')) { throw 'Root does not match the installed AlShifaApi service; nothing was stopped.' }
if ($hostConfig.Mirror.Enabled) { throw 'Use the online deployment workflow for an online server.' }
Assert-UpgradeEnvironment $hostConfig $Root
$existingSync = Get-EstablishedSyncUpgradeSnapshot $hostConfig $Root
# Both manual and automatic upgrades must keep an established worker held until
# health passes, then restore its exact original settings without enrollment.
$holdSync = $HoldOnlineServices -or $null -ne $existingSync
Ensure-VisualCppRuntime (Join-Path $Root 'mysql\bin\mysqld.exe') (Join-Path $Root 'backup-staging\runtime-download') (Join-Path $release 'prerequisites') ''
$connection = New-Object System.Data.Common.DbConnectionStringBuilder
# PowerShell's dictionary adapter treats property assignment as a dictionary key
# on this type. Call the CLR setter so Server/Database/Port are actually parsed.
$connection.set_ConnectionString([string]$hostConfig.ConnectionStrings.Default)
if ([string]$connection['Server'] -notin @('127.0.0.1', 'localhost') -or [string]$connection['Database'] -ne 'alshifa') { throw 'The updater requires the verified local hospital database configuration.' }
$backupClient = @{}
foreach ($line in (Get-Content -LiteralPath (Join-Path $Root 'secrets\backup.cnf'))) {
    if ($line -match '^(host|port)=(.*)$') { $backupClient[$Matches[1]] = $Matches[2].Trim() }
}
if ($backupClient.host -notin @('127.0.0.1', 'localhost') -or [int]$backupClient.port -ne [int]$connection['Port']) { throw 'Backup target and running API database endpoint do not match.' }
$mysqlService=Get-CimInstance Win32_Service -Filter "Name='AlShifaMySQL'"
$mysqlExe=Join-Path $Root 'mysql\bin\mysqld.exe'
if(-not $mysqlService -or $mysqlService.PathName -notmatch ('^"?'+[regex]::Escape($mysqlExe)+'"?(\s|$)')){throw 'MySQL service does not belong to this installation; nothing was stopped.'}
$identity=@(Invoke-Native (Join-Path $Root 'mysql\bin\mysql.exe') @("--defaults-extra-file=$(Join-Path $Root 'secrets\backup.cnf')",'-N','-B','-e','SELECT @@port, @@datadir;'))[-1] -split "`t"
if([int]$identity[0] -ne [int]$connection['Port'] -or [IO.Path]::GetFullPath($identity[1]).TrimEnd('\') -ne (Join-Path $Root 'data')){throw 'Database identity differs from the installed service; nothing was stopped.'}
$keyDirectory = [string]$hostConfig.DataProtection.KeysPath
if (-not $keyDirectory) { throw 'DataProtection:KeysPath is required for a safe maintenance barrier.' }
$maintenanceFile = Join-Path $keyDirectory 'update-maintenance'
[IO.File]::WriteAllText($maintenanceFile, $runId)

# Stopped first, so nothing entered after the backup is lost if the backup has to be restored.
'== Stop the application'
try { Stop-Service AlShifaApi } catch { Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue; throw }

'== Database integrity check'
# A damaged database is never upgraded or backed up over: the old version keeps running and nothing is changed.
try {
    $integrityProblems = @(Test-AlShifaDatabaseIntegrity $Root -Deep)
    if ($integrityProblems.Count) {
        $integrityProblems | ForEach-Object { "DAMAGE: $_" }
        throw "The database shows signs of damage ($($integrityProblems.Count) finding(s)). Run Rebuild-Database.bat from the installer folder first; it rebuilds the database files from the current data without deleting anything."
    }
    'Database integrity: OK (recovery mode off, no corruption in the MySQL log, CHECK TABLE passed on every table)'
    # Lock waits fail after 20 s instead of 50 s, so a blocked save reports "busy" instead of freezing the screen. Kept across restarts.
    # performance_schema off (from the next MySQL restart) frees 200-400 MB of memory nobody uses here.
    $rootCnf = Join-Path $Root "secrets\update-$runId.cnf"
    try {
        Write-ClientCnf $rootCnf 'root' (Get-Content (Join-Path $Root 'secrets\mysql-root.txt') -Raw).Trim() ([int]$connection['Port'])
        Invoke-Native (Join-Path $Root 'mysql\bin\mysql.exe') @("--defaults-extra-file=$rootCnf", '-e', 'SET PERSIST innodb_lock_wait_timeout=20; SET PERSIST_ONLY performance_schema=OFF;') | Out-Null
    } catch { "WARNING: innodb_lock_wait_timeout was not changed: $_" } finally { Remove-Item -LiteralPath $rootCnf -Force -ErrorAction SilentlyContinue }
} catch {
    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    Start-Service AlShifaApi
    throw "The update was not started and the current version is running again. Reason: $_"
}

'== Backup before changing anything'
try {
    $sourceProof=Get-UpgradeDataProof (Join-Path $Root 'mysql\bin\mysql.exe') (Join-Path $Root 'secrets\backup.cnf') 'alshifa' -ReadOnlySource
    $backupOutput = & (Join-Path $PSScriptRoot 'backup.ps1') -Root $Root -Destination $BackupDestination -AllowSameDisk:$AllowSameDisk
    $backupOutput
    if ($LASTEXITCODE) { throw 'The backup failed.' }
    $line = @($backupOutput | ForEach-Object { "$_" } | Where-Object { $_ -match '^Backup complete: (.+)$' }) | Select-Object -Last 1
    if (-not $line) { throw 'The backup did not report its file.' }
    $backupFile = [regex]::Match($line, '^Backup complete: (.+)$').Groups[1].Value
    # backup.ps1 now performs the restore drill before reporting success. Require proof for this exact archive.
    $backupStatus = Get-Content -LiteralPath (Join-Path $Root 'logs\backup-status.json') -Raw | ConvertFrom-Json
    if (-not $backupStatus.restoreVerifiedAt -or $backupStatus.restoreVerifiedFile -ne (Split-Path $backupFile -Leaf)) { throw 'The exact pre-update backup has no verified restore result.' }
    # Execute the NEW migrations on a second disposable restoration first. A
    # rejected migration or change to protected rows must never reach the live DB.
    & (Join-Path $PSScriptRoot 'restore-test.ps1') -Backup $backupFile -Root $Root -CandidateApp (Join-Path $release 'app') -ExpectedSourceProof $sourceProof | Out-Null
} catch {
    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    Start-Service AlShifaApi
    throw "The update was not started and the current version is running again. Reason: $_"
}

'== Swap the application files'
$swapped=$false
try {
    if (Test-Path -LiteralPath $previous) { throw 'Rollback directory already exists.' }
    Move-Item -LiteralPath $appDir -Destination $previous
    $swapped=$true
    New-Item -ItemType Directory $appDir | Out-Null
    Copy-Item (Join-Path $release 'app\*') $appDir -Recurse
    Copy-Item (Join-Path $previous 'appsettings.Production.json') $appDir
    $updatedConfigPath = Join-Path $appDir 'appsettings.Production.json'
    $updatedConfig = Get-Content -LiteralPath $updatedConfigPath -Raw | ConvertFrom-Json
    if (-not $updatedConfig.PSObject.Properties['Update']) { $updatedConfig | Add-Member -NotePropertyName Update -NotePropertyValue ([pscustomobject]@{}) }
    $updatedConfig.Update | Add-Member -NotePropertyName StatusFile -NotePropertyValue (Join-Path $Root 'logs\update-status.json') -Force
    $updatedConfig.Update | Add-Member -NotePropertyName ApprovalFile -NotePropertyValue (Join-Path $Root 'requests\update-approval.json') -Force
    if($holdSync){
        if(-not $updatedConfig.PSObject.Properties['Sync']){$updatedConfig | Add-Member -NotePropertyName Sync -NotePropertyValue ([pscustomobject]@{})}
        $updatedConfig.Sync | Add-Member -NotePropertyName RequireCompatibilityApproval -NotePropertyValue $true -Force
    }
    [IO.File]::WriteAllText($updatedConfigPath, ($updatedConfig | ConvertTo-Json -Depth 32))
    Protect-Path $appDir @('NT SERVICE\AlShifaApi=RX')
    Copy-Item (Join-Path $PSScriptRoot '*') (Join-Path $Root 'scripts') -Recurse -Force
    foreach ($old in 'director-access.ps1', 'remote-access.ps1') { Remove-Item (Join-Path $Root "scripts\$old") -Force -ErrorAction SilentlyContinue }
    Add-BackupRequestTrigger
    Set-NightlyBackupSchedule
    Set-AutoUpdateTasks $Root
} catch {
    # The database has not been touched yet, so going back to the old files is complete and safe.
    if($swapped){
        if (Test-Path -LiteralPath $appDir) { Move-Item -LiteralPath $appDir -Destination $failed }
        Move-Item -LiteralPath $previous -Destination $appDir
    }
    Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
    Start-Service AlShifaApi
    throw "Copying the new version failed; the old version is running again. Reason: $_"
}

'== Migrate'
Push-Location $appDir
try {
    Invoke-Native (Join-Path $appDir 'AlShifa.Api.exe') @('--migrate', '--backup-taken') | Out-Null
} catch {
    $reason = $_
    Pop-Location
    # A failed migration can leave the schema half changed. The backup taken and restore-tested above goes back,
    # with the previous version of the program, so the hospital is never left with a half-installed system.
    "Migration failed: $reason"
    '== Automatic rollback: the database from the backup above, then the previous version'
    try {
        Restore-AlShifaDatabase $Root $backupFile
        Move-Item -LiteralPath $appDir -Destination $failed
        Move-Item -LiteralPath $previous -Destination $appDir
        Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
        if ($existingSync) { Assert-EstablishedSyncIdentity $existingSync }
        Start-Service AlShifaApi
        Start-Sleep -Seconds 5
        Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Root 'scripts\health-check.ps1'), '-Root', $Root, '-Maintenance') | Out-Null
    } catch {
        Stop-Service AlShifaApi -ErrorAction SilentlyContinue
        throw "Migration failed and the automatic rollback failed too. The application is stopped. Follow 'Rollback' in DEPLOYMENT-WINDOWS.md with $backupFile. Migration: $reason. Rollback: $_"
    }
    throw "The update failed and was undone: the previous version runs on the database as it was before the update ($backupFile). Reason: $reason"
}
Pop-Location

# Keep Sync held until application health has passed. A worker can otherwise
# send changes during the health check, making a database rollback unsafe.
try {
    Start-Service AlShifaApi
    Start-Sleep -Seconds 5
    Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Root 'scripts\health-check.ps1'), '-Root', $Root, '-Maintenance') | Out-Null
    if ($existingSync) {
        Restore-EstablishedSyncUpgradeConfiguration $existingSync $updatedConfigPath
        Restart-Service AlShifaApi -ErrorAction Stop
        Start-Sleep -Seconds 5
        Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Root 'scripts\health-check.ps1'), '-Root', $Root, '-Maintenance') | Out-Null
        Write-EstablishedSyncUpgradeStatus $Root
    }
} catch {
    $reason = $_
    # No staff writes are allowed during the maintenance window. Stop before restoring the pre-update database.
    Stop-Service AlShifaApi -ErrorAction SilentlyContinue
    try {
        Restore-AlShifaDatabase $Root $backupFile
        Move-Item -LiteralPath $appDir -Destination $failed
        Move-Item -LiteralPath $previous -Destination $appDir
        Remove-Item -LiteralPath $maintenanceFile -Force -ErrorAction SilentlyContinue
        if ($existingSync) { Assert-EstablishedSyncIdentity $existingSync }
        Start-Service AlShifaApi
        Start-Sleep -Seconds 5
        Invoke-Native 'powershell.exe' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $Root 'scripts\health-check.ps1'), '-Root', $Root, '-Maintenance') | Out-Null
    } catch {
        Stop-Service AlShifaApi -ErrorAction SilentlyContinue
        throw 'Post-update health and rollback failed. Service is stopped; protected backup and previous application are retained for recovery.'
    }
    throw "Post-update health failed; the previous application and pre-update database were restored. Reason: $reason"
}
Remove-Item -LiteralPath $maintenanceFile -Force
if($HoldOnlineServices -and -not $existingSync){
    & (Join-Path $PSScriptRoot 'sync-bootstrap.ps1') -Root $Root
    . (Join-Path $PSScriptRoot 'sync-bootstrap-common.ps1')
    Set-SyncBootstrapTask $Root
}
# Later approved versions arrive by themselves (auto-update.ps1). A problem here never undoes a verified update.
try {
    . (Join-Path $PSScriptRoot 'auto-update-common.ps1')
    Enable-PublisherFeed $Root $PSScriptRoot $BackupDestination ([bool]$AllowSameDisk)
} catch { "WARNING: automatic updates were not enrolled: $_" }
# Old program copies only (no data): the two most recent stay for going back by hand.
Get-ChildItem -LiteralPath $Root -Directory -Filter 'app.previous-*' | Sort-Object Name -Descending | Select-Object -Skip 2 |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
Get-ChildItem -LiteralPath $Root -Directory -Filter 'app.failed-*' | Sort-Object Name -Descending | Select-Object -Skip 1 |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
"Update verified. Previous application retained at $previous."
