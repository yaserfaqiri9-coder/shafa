# Preserve an already configured installation. Never enroll a source, change
# checkpoints, enable a paused database worker, or contact the online server.
function Get-EstablishedSyncUpgradeSnapshot($Config, [string]$Root) {
    if ($Config.Mirror.Enabled -or -not $Config.Sync -or $Config.Sync.RequireCompatibilityApproval) { return $null }
    $uri = $null
    if (-not [uri]::TryCreate([string]$Config.Sync.Url, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or
        ([string]$Config.Sync.Key).Length -lt 32) { return $null }
    $keys = [IO.Path]::GetFullPath([string]$Config.DataProtection.KeysPath).TrimEnd('\')
    $identity = if ($Config.Sync.IdentityFile) { [IO.Path]::GetFullPath([string]$Config.Sync.IdentityFile) } else { Join-Path $keys 'sync-installation-id' }
    if ($keys -ne (Join-Path $Root 'keys') -or -not $identity.StartsWith($keys + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Existing Sync identity is outside the verified backup mapping; nothing may recreate or replace it.'
    }
    $id = [guid]::Empty
    if (-not (Test-Path -LiteralPath $identity -PathType Leaf) -or
        -not [guid]::TryParse((Get-Content -LiteralPath $identity -Raw).Trim(), [ref]$id) -or $id -eq [guid]::Empty) {
        throw 'Existing Sync identity is missing or invalid. Stop the upgrade; do not generate another identity.'
    }
    [pscustomobject]@{
        Settings = ($Config.Sync | ConvertTo-Json -Depth 32 -Compress)
        IdentityPath = $identity
        IdentityHash = (Get-FileHash -LiteralPath $identity -Algorithm SHA256).Hash
    }
}

function Assert-EstablishedSyncIdentity($Snapshot) {
    if (-not $Snapshot -or -not (Test-Path -LiteralPath $Snapshot.IdentityPath -PathType Leaf) -or
        (Get-FileHash -LiteralPath $Snapshot.IdentityPath -Algorithm SHA256).Hash -cne $Snapshot.IdentityHash) {
        throw 'Existing Sync identity changed during upgrade; do not resume synchronization.'
    }
}

function Restore-EstablishedSyncUpgradeConfiguration($Snapshot, [string]$ConfigFile) {
    Assert-EstablishedSyncIdentity $Snapshot
    $config = Get-Content -LiteralPath $ConfigFile -Raw | ConvertFrom-Json
    $original = $Snapshot.Settings | ConvertFrom-Json
    $expectedHeld = $Snapshot.Settings | ConvertFrom-Json
    $expectedHeld | Add-Member -NotePropertyName RequireCompatibilityApproval -NotePropertyValue $true -Force
    if (($config.Sync | ConvertTo-Json -Depth 32 -Compress) -cne ($expectedHeld | ConvertTo-Json -Depth 32 -Compress)) {
        throw 'Sync settings changed beyond the temporary maintenance hold; stop for review.'
    }
    $config.Sync = $original
    $temporary = $ConfigFile + '.sync-restore-' + [guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporary, ($config | ConvertTo-Json -Depth 32), (New-Object Text.UTF8Encoding($false)))
        Move-Item -LiteralPath $temporary -Destination $ConfigFile -Force
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}

function Write-EstablishedSyncUpgradeStatus([string]$Root) {
    $status = @{ Ready = $true; Reason = 'existing_configuration_preserved'; ExistingConfigurationPreserved = $true; RealSyncAccepted = $false; CheckedAt = [DateTimeOffset]::UtcNow.ToString('o') }
    [IO.File]::WriteAllText((Join-Path $Root 'logs\sync-bootstrap-status.json'), ($status | ConvertTo-Json))
}
