# SYSTEM worker: checks every minute. A signed, verified newer version installs only after an explicit, version-specific
# administrator approval. update.ps1 backs up, proves the
# backup restores, migrates a disposable copy first and rolls back the application and database on any failure.
param([string]$Root = 'C:\AlShifa', [switch]$InstallApproved)
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'auto-update-common.ps1')
$statusPath = Join-Path $Root 'logs\update-status.json'
$settingsPath = Join-Path $Root 'secrets\auto-update.json'
$cache = Join-Path $Root 'updates'
$lock = $null
$status = [ordered]@{ configured = $false; state = 'unconfigured'; checkedAt = (Get-Date).ToUniversalTime().ToString('o'); lastError = $null }
# Failure memory survives checks. Installation is never retried without another approval.
$previousStatus = $null
try { if (Test-Path -LiteralPath $statusPath) { $previousStatus = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json } } catch { }
foreach ($kept in 'failedVersion', 'failedAttempts', 'failedAt', 'installedAt') {
    if ($previousStatus -and $previousStatus.PSObject.Properties[$kept] -and $null -ne $previousStatus.$kept) { $status[$kept] = $previousStatus.$kept }
}
$stage = 'configuration'
function Save-UpdateStatus {
    $temporary = "$statusPath.tmp"
    [IO.File]::WriteAllText($temporary, ($status | ConvertTo-Json -Depth 4))
    Move-Item -LiteralPath $temporary -Destination $statusPath -Force
}
try {
    New-Item -ItemType Directory -Force $cache, (Split-Path $statusPath) | Out-Null
    try { $lock = [IO.File]::Open((Join-Path $cache 'worker.lock'), 'OpenOrCreate', 'ReadWrite', 'None') } catch { exit 0 }
    if (-not (Test-Path -LiteralPath $settingsPath)) { Save-UpdateStatus; exit 0 }
    # Downloading a signed update must remain possible while Sync is held. The
    # installed worker retains its own identity/schema/protocol checks throughout.
    $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $status.configured = $true
    $feed = [uri]$settings.manifestUrl
    $manifestPath = Join-Path $cache 'manifest.json'
    $signaturePath = Join-Path $cache 'manifest.sig'
    $stage = 'download-manifest'
    Receive-UpdateFile $feed $manifestPath 65536
    Receive-UpdateFile ([uri]($feed.AbsoluteUri + '.sig')) $signaturePath 8192
    $stage = 'verify-signature'
    $manifest = Assert-UpdateManifest ([IO.File]::ReadAllBytes($manifestPath)) ([Convert]::FromBase64String([IO.File]::ReadAllText($signaturePath).Trim())) (Join-Path $Root 'secrets\update-public.xml')
    $packageUri = [uri]$manifest.packageUrl
    if ($packageUri.Scheme -ne 'https' -or $packageUri.Authority -ne $feed.Authority) { throw 'Package must use the approved publisher HTTPS host.' }
    $installedPath = Join-Path $Root 'app\release.json'
    $stage = 'installed-version'
    if (-not (Test-Path -LiteralPath $installedPath)) { throw 'Installed version marker is missing; technical enrollment is required.' }
    $installed = Get-Content -LiteralPath $installedPath -Raw | ConvertFrom-Json
    $status.currentVersion = [string]$installed.version
    $status.availableVersion = [string]$manifest.version
    if ([version]$manifest.version -le [version]$installed.version) { $status.state = 'current'; Save-UpdateStatus; exit 0 }
    $package = Join-Path $cache ('alshifa-' + $manifest.version + '.zip')
    $stage = 'download-package'
    if (-not (Test-Path -LiteralPath $package) -or (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $manifest.sha256) {
        $status.state = 'downloading'; Save-UpdateStatus
        Receive-UpdateFile $packageUri ($package + '.partial') ([long]$manifest.sizeBytes)
        if ((Get-Item -LiteralPath ($package + '.partial')).Length -ne [long]$manifest.sizeBytes -or (Get-FileHash -LiteralPath ($package + '.partial') -Algorithm SHA256).Hash -ne $manifest.sha256) { throw 'Update package hash or length mismatch.' }
        Move-Item -LiteralPath ($package + '.partial') -Destination $package -Force
    }
    $destination = Join-Path $cache ('release-' + $manifest.version + '-' + [Guid]::NewGuid().ToString('N'))
    $stage = 'verify-archive'
    Assert-UpdateArchive $package $destination
    $status.state = 'awaitingApproval'
    $status.packageVerified = $true
    Save-UpdateStatus
    # An approval event can arrive while a check owns worker.lock. The next
    # minute also consumes the protected request, so the click is not lost.
    $approvalFile = Join-Path $Root 'requests\update-approval.json'
    if (-not $InstallApproved -and -not (Test-Path -LiteralPath $approvalFile)) { exit 0 }
    $stage = 'approval'
    try { Assert-UpdateApproval $approvalFile ([string]$manifest.version) } catch {
        if ($InstallApproved) { throw }
        exit 0
    }
    $approval = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'AlShifa.Api'; Id = 4101 } -MaxEvents 1 -ErrorAction Stop
    if ($approval.TimeCreated.ToUniversalTime() -lt (Get-Date).ToUniversalTime().AddMinutes(-10) -or $approval.Message.Trim() -ne "AlShifa update approval $($manifest.version)") { throw 'Recent administrator approval for this exact version is required.' }
    # Local event writers cannot authorize an installation; only the API service can write this protected file.
    Move-Item -LiteralPath $approvalFile -Destination (Join-Path $cache ('approval-consumed-' + [Guid]::NewGuid().ToString('N') + '.json'))
    [IO.Compression.ZipFile]::ExtractToDirectory($package, $destination)
    $release = Get-Content -LiteralPath (Join-Path $destination 'app\release.json') -Raw | ConvertFrom-Json
    if ($release.version -ne $manifest.version) { throw 'Signed release version does not match package.' }
    if (-not $manifest.schema -or $release.schema -ne $manifest.schema) { throw 'Signed schema does not match package.' }
    # Recheck immediately before execution. Only trusted, signed package scripts run with SYSTEM privileges.
    if ((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash -ne $manifest.sha256) { throw 'Update package changed.' }
    $status.state = 'installing'
    $status.attemptVersion = [string]$manifest.version
    Save-UpdateStatus
    $stage = 'install-and-health-check'
    $updateArguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $destination 'scripts\update.ps1'), '-Root', $Root, '-BackupDestination', [string]$settings.backupDestination)
    if ($settings.allowSameDisk -eq $true) { $updateArguments += '-AllowSameDisk' }
    Invoke-Native 'powershell.exe' $updateArguments | Out-Null
    $status.currentVersion = [string]$manifest.version
    $status.state = 'installed'
    $status.installedAt = (Get-Date).ToUniversalTime().ToString('o')
    foreach ($cleared in 'failedVersion', 'failedAttempts', 'failedAt', 'attemptVersion') { $status.Remove($cleared) }
    Save-UpdateStatus
    Remove-OldUpdateFiles $cache ([string]$manifest.version)
} catch {
    if ($status.Contains('attemptVersion')) {
        # update.ps1 has already put back the previous version (or stopped the service if that failed too).
        $attempts = if ($status.failedVersion -eq $status.attemptVersion) { [int]$status.failedAttempts + 1 } else { 1 }
        $status.failedVersion = $status.attemptVersion
        $status.failedAttempts = $attempts
        $status.failedAt = (Get-Date).ToUniversalTime().ToString('o')
        $status.Remove('attemptVersion')
    }
    $status.state = 'failed'
    # Avoid persisting command output, connection strings or host credentials from an updater exception.
    $status.lastError = "Update failed at stage: $stage. No successful installation is confirmed."
    Save-UpdateStatus
    Write-Error 'Auto update failed; current state is recorded in update-status.json.' -ErrorAction Continue
    exit 1
} finally { if ($lock) { $lock.Dispose() } }
