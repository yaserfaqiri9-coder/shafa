# Runs deploy\windows\finish-interrupted-update.ps1 against synthetic installations for each interruption point.
# Services, SQL, migration, health and restore are replaced by recorders; no service, database or network is touched.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$repo = Split-Path $PSScriptRoot -Parent
$work = Join-Path $repo ('.tools\finish-update-tests-' + [Guid]::NewGuid().ToString('N'))
$scripts = Join-Path $work 'scripts'
New-Item -ItemType Directory -Force $scripts | Out-Null
$source = (Get-Content (Join-Path $repo 'deploy\windows\finish-interrupted-update.ps1') -Raw) -replace '(?m)^#Requires -RunAsAdministrator\r?\n', ''
[IO.File]::WriteAllText((Join-Path $scripts 'finish-interrupted-update.ps1'), $source)
Copy-Item (Join-Path $repo 'deploy\windows\sync-upgrade-common.ps1') $scripts
[IO.File]::WriteAllText((Join-Path $scripts 'db-integrity.ps1'), 'function Test-AlShifaDatabaseIntegrity { @() }')
[IO.File]::WriteAllText((Join-Path $scripts 'common.ps1'), @'
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Get-Service([string[]]$Name) { $Name | ForEach-Object { [pscustomobject]@{ Name = $_; Status = 'Running' } } }
function Start-Service { }
function Stop-Service { $global:T.Stopped++ }
function Restart-Service { $global:T.Restarts++ }
function Invoke-AlShifaSql([string]$Sql) {
    if ($Sql -eq 'SELECT 1') { return '1' }
    if ($Sql -like '*__EFMigrationsHistory*') { return $global:T.Migration }
    if ($Sql -like '*information_schema.COLUMNS*') { return "$($global:T.Columns)" }
    throw "unexpected SQL $Sql"
}
function Test-AlShifaHealthy { if (Test-Path -LiteralPath $global:T.Lock) { $global:T.HealthUnderLock++ }; return -not $global:T.Unhealthy }
function Restore-AlShifaDatabase($Root, $File) { $global:T.Restored = $File }
function Invoke-Native($Exe, [string[]]$Arguments) { if ($Arguments -contains '--migrate') { if ($global:T.MigrateFails) { throw 'synthetic migration failure' }; $global:T.Migrated++ } }
function Protect-Path { }
function Add-BackupRequestTrigger { }
function Set-NightlyBackupSchedule { }
function Set-AutoUpdateTasks { }
'@)
$package = Join-Path $work 'package'
New-Item -ItemType Directory -Force (Join-Path $package 'app'), (Join-Path $package 'scripts') | Out-Null
'new' | Set-Content (Join-Path $package 'app\marker.txt')
'exe' | Set-Content (Join-Path $package 'app\AlShifa.Api.exe')
'#' | Set-Content (Join-Path $package 'scripts\update.ps1')

# An installer in the real layout (program, zip, length, SHA-256, ALSHIFA1) beside the tool folder.
$zipFile = Join-Path $work 'fake.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($package, $zipFile)
$zipBytes = [IO.File]::ReadAllBytes($zipFile)
$fake = New-Object IO.MemoryStream
$fake.Write([byte[]](77, 90, 0, 0), 0, 4)
$fake.Write($zipBytes, 0, $zipBytes.Length)
$fake.Write([BitConverter]::GetBytes([long]$zipBytes.Length), 0, 8)
$digest = [Security.Cryptography.SHA256]::Create().ComputeHash($zipBytes); $fake.Write($digest, 0, 32)
$fake.Write([Text.Encoding]::ASCII.GetBytes('ALSHIFA1'), 0, 8)
[IO.File]::WriteAllBytes((Join-Path $work 'Install-AlShifa.exe'), $fake.ToArray())

$new = '20261125105153_OpticalOrderCustomerName'; $old = '20261124084651_OpticalOrderSettlementAndStockAudit'; $run = '20261125T155046Z-1a2b3c4d'
$cases = @(
    @{ Name = 'A'; Previous = $false; Migration = $old; Columns = 0 },
    @{ Name = 'B'; Previous = $true; Migration = $new; Columns = 2 },
    @{ Name = 'B-unhealthy'; Previous = $true; Migration = $new; Columns = 2; Unhealthy = $true },
    @{ Name = 'C'; Previous = $true; Migration = $old; Columns = 0 },
    @{ Name = 'C-from-installer'; Previous = $true; Migration = $old; Columns = 0; FromInstaller = $true },
    @{ Name = 'C-migration-fails'; Previous = $true; Migration = $old; Columns = 0; MigrateFails = $true },
    @{ Name = 'D'; Previous = $true; Migration = $old; Columns = 1 },
    @{ Name = 'stale-backup'; Previous = $true; Migration = $old; Columns = 0; Stale = $true },
    @{ Name = 'unknown'; Previous = $false; Migration = $new; Columns = 2 }
)
foreach ($case in $cases) {
    $root = Join-Path $work $case.Name
    foreach ($d in 'app', 'keys', 'logs', 'backups', 'scripts') { New-Item -ItemType Directory -Force (Join-Path $root $d) | Out-Null }
    $identity = Join-Path $root 'keys\sync-installation-id'
    [IO.File]::WriteAllText($identity, '0ab50744-3dc0-40d4-9662-50c1332ca4c2')
    $identityHash = (Get-FileHash $identity).Hash
    $sync = @{ Url = 'https://shafa.novatechsoft.com'; Key = ('K' * 40); IntervalMinutes = 5 }
    $config = @{ ConnectionStrings = @{ Default = 'x' }; DataProtection = @{ KeysPath = (Join-Path $root 'keys') }; Mirror = @{ Enabled = $false }; Kestrel = @{ Endpoints = @{ Loopback = @{ Url = 'http://127.0.0.1:1' } } }; Sync = $sync }
    if ($case.Previous) {
        New-Item -ItemType Directory (Join-Path $root "app.previous-$run") | Out-Null
        [IO.File]::WriteAllText((Join-Path $root "app.previous-$run\appsettings.Production.json"), ($config | ConvertTo-Json -Depth 8))
        'old' | Set-Content (Join-Path $root "app.previous-$run\marker.txt")
        # Exactly as update.ps1 writes it: the previous file with the hold added to Sync.
        $held = Get-Content (Join-Path $root "app.previous-$run\appsettings.Production.json") -Raw | ConvertFrom-Json
        $held.Sync | Add-Member -NotePropertyName RequireCompatibilityApproval -NotePropertyValue $true -Force
        [IO.File]::WriteAllText((Join-Path $root 'app\appsettings.Production.json'), ($held | ConvertTo-Json -Depth 32))
        $(if ($case.Migration -eq $new) { 'new' } else { 'partial' }) | Set-Content (Join-Path $root 'app\marker.txt')
    } else {
        [IO.File]::WriteAllText((Join-Path $root 'app\appsettings.Production.json'), ($config | ConvertTo-Json -Depth 8))
        'old' | Set-Content (Join-Path $root 'app\marker.txt')
    }
    $lock = Join-Path $root 'keys\update-maintenance'
    [IO.File]::WriteAllText($lock, $run)
    $verifiedAt = (Get-Date).ToUniversalTime().AddMinutes($(if ($case.Stale) { -60 } else { 5 })).ToString('o')
    @{ restoreVerifiedAt = $verifiedAt; restoreVerifiedFile = 'alshifa-pre.zip.gpg'; destination = (Join-Path $root 'backups') } | ConvertTo-Json | Set-Content (Join-Path $root 'logs\backup-status.json')
    'x' | Set-Content (Join-Path $root 'backups\alshifa-pre.zip.gpg')
    $global:T = @{ Migration = $case.Migration; Columns = $case.Columns; Unhealthy = [bool]$case.Unhealthy; MigrateFails = [bool]$case.MigrateFails; Migrated = 0; Lock = $lock; Stopped = 0; Restarts = 0; HealthUnderLock = 0; Restored = $null }

    # Report only must change nothing.
    $before = (Get-ChildItem $root -Recurse -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ','
    try { $report = & (Join-Path $scripts 'finish-interrupted-update.ps1') -Root $root -Package $package 2>&1 | Out-String } catch { $report = "$_" }
    if ((((Get-ChildItem $root -Recurse -File | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join ',') -ne $before) -or $global:T.Restarts) { throw "FAIL $($case.Name): report changed something" }
    if ($case.Name -eq 'unknown') {
        if ($report -notmatch 'not one the tool knows') { throw 'FAIL: unknown state not refused' }
        'PASS: unknown state refused, nothing changed'; continue
    }
    if ($case.Name -eq 'stale-backup') {
        if ($report -notmatch 'verified backup of this update was not found') { throw "FAIL: stale backup accepted $report" }
        'PASS: no fresh verified backup, refused, nothing changed'; continue
    }
    $failed = $false; $err = ''
    $pkgArg = if ($case.FromInstaller) { $null } else { $package }
    $savedPf = $env:ProgramFiles; if ($case.FromInstaller) { $env:ProgramFiles = Join-Path $work 'no-program-files' }
    try { & (Join-Path $scripts 'finish-interrupted-update.ps1') -Root $root -Apply -Package $pkgArg | Out-Null } catch { $failed = $true; $err = "$_" } finally { $env:ProgramFiles = $savedPf }
    $marker = (Get-Content (Join-Path $root 'app\marker.txt')).Trim()
    $cfg = Get-Content (Join-Path $root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json
    $restoredSync = -not $cfg.Sync.RequireCompatibilityApproval -and $cfg.Sync.Key -eq $sync.Key -and $cfg.Sync.Url -eq $sync.Url
    switch ($case.Name) {
        'A' { if ($failed -or (Test-Path $lock) -or $marker -ne 'new' -or $global:T.Migrated -ne 1 -or -not $restoredSync -or $global:T.Restored -or $global:T.HealthUnderLock -lt 1 -or (Get-Content (Join-Path $root "app.previous-$run\marker.txt")).Trim() -ne 'old') { throw "FAIL A $err" } }
        'B' { if ($failed -or (Test-Path $lock) -or $marker -ne 'new' -or $global:T.Migrated -ne 0 -or -not $restoredSync -or $global:T.Restored -or $global:T.HealthUnderLock -lt 1) { throw "FAIL B $err" } }
        'B-unhealthy' { if (-not $failed -or $marker -ne 'old' -or -not $global:T.Restored -or -not (Test-Path (Join-Path $root "app.failed-$run"))) { throw 'FAIL B-unhealthy' } }
        'C' { if ($failed -or (Test-Path $lock) -or $marker -ne 'new' -or $global:T.Migrated -ne 1 -or -not $restoredSync -or $global:T.Restored -or (Get-Content (Join-Path $root "app.failed-$run\marker.txt")).Trim() -ne 'partial' -or $global:T.HealthUnderLock -lt 1) { throw "FAIL C $err" } }
        'C-from-installer' { if ($failed -or (Test-Path $lock) -or $marker -ne 'new' -or $global:T.Migrated -ne 1 -or -not $restoredSync) { throw "FAIL C-from-installer $err" } }
        'C-migration-fails' { if (-not $failed -or $marker -ne 'old' -or -not $global:T.Restored) { throw "FAIL C-migration-fails $err" } }
        'D' { if ($failed -or (Test-Path $lock) -or $marker -ne 'old' -or $global:T.Restored -notlike '*alshifa-pre.zip.gpg') { throw "FAIL D $err" } }
    }
    if ((Get-FileHash $identity).Hash -ne $identityHash) { throw 'FAIL: sync identity changed' }
    "PASS: case $($case.Name)"
}
Remove-Item $work -Recurse -Force
