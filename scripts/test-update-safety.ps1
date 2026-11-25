# Offline adversarial tests with synthetic packages and ephemeral keys; never calls a Windows service or database.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')
. (Join-Path $repo 'deploy\windows\auto-update-common.ps1')
$work = Join-Path $repo ('.tools\update-safety-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
function Assert-Refused([scriptblock]$Action, [string]$Name) {
    $refused = $false
    try { & $Action | Out-Null } catch { $refused = $true }
    if (-not $refused) { throw "FAIL: $Name was accepted." }
    "PASS: $Name refused."
}
try {
    $connection = New-Object System.Data.Common.DbConnectionStringBuilder
    $connection.set_ConnectionString('Server=127.0.0.1;Port=33490;Database=alshifa;User=synthetic;Password=synthetic')
    if ($connection['Server'] -ne '127.0.0.1' -or $connection['Database'] -ne 'alshifa' -or [int]$connection['Port'] -ne 33490) { throw 'Updater endpoint parsing failed.' }
    'PASS: PowerShell parses the actual updater database endpoint.'
    $public = Join-Path $work 'public.xml'
    [IO.File]::WriteAllText($public, $rsa.ToXmlString($false))
    $bytes = [Text.Encoding]::UTF8.GetBytes('{"version":"2026.11.21.1","sha256":"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA","sizeBytes":128}')
    $signature = $rsa.SignData($bytes, 'SHA256')
    $valid = Assert-UpdateManifest $bytes $signature $public
    if ($valid.version -ne '2026.11.21.1') { throw 'Valid manifest verification failed.' }
    'PASS: authentic signed manifest accepted.'
    $tampered = $bytes.Clone(); $tampered[15] = $tampered[15] -bxor 1
    Assert-Refused { Assert-UpdateManifest $tampered $signature $public } 'altered manifest'
    $other = New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
    try { Assert-Refused { Assert-UpdateManifest $bytes ($other.SignData($bytes, 'SHA256')) $public } 'untrusted publisher' } finally { $other.Dispose() }
    Assert-Refused { Receive-UpdateFile ([uri]'http://localhost/manifest.json') (Join-Path $work 'insecure') 100 } 'unencrypted update transport'
    $zipPath = Join-Path $work 'traversal.zip'
    $zip = [IO.Compression.ZipFile]::Open($zipPath, 'Create')
    try { [void]$zip.CreateEntry('../escape.txt') } finally { $zip.Dispose() }
    Assert-Refused { Assert-UpdateArchive $zipPath (Join-Path $work 'unpack') } 'archive directory traversal'
    $release = Join-Path $work 'release'
    foreach ($folder in 'app', 'scripts') { New-Item -ItemType Directory -Force (Join-Path $release $folder) | Out-Null }
    [IO.File]::WriteAllText((Join-Path $release 'app\AlShifa.Api.exe'), 'synthetic executable placeholder - never executed')
    [IO.File]::WriteAllText((Join-Path $release 'app\release.json'), '{"version":"2026.11.21.1"}')
    [IO.File]::WriteAllText((Join-Path $release 'scripts\update.ps1'), '# synthetic script - never executed')
    $safeZip = Join-Path $work 'safe.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($release, $safeZip)
    Assert-UpdateArchive $safeZip (Join-Path $work 'safe-unpack')
    'PASS: complete synthetic archive accepted.'
    [IO.File]::WriteAllText((Join-Path $release 'app\appsettings.Production.json'), '{"syntheticSecret":"test-only"}')
    $secretZip = Join-Path $work 'secret.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($release, $secretZip)
    Assert-Refused { Assert-UpdateArchive $secretZip (Join-Path $work 'secret-unpack') } 'release containing host configuration'
    Remove-Item -LiteralPath (Join-Path $release 'app\appsettings.Production.json') -Force
    $privateKey = Join-Path $work 'synthetic-private.xml'
    [IO.File]::WriteAllText($privateKey, $rsa.ToXmlString($true))
    try {
        $published = Join-Path $work 'published'
        & (Join-Path $repo 'deploy\windows\sign-update.ps1') -Release $release -PrivateKeyPath $privateKey -Output $published -PackageUrl 'https://publisher.example/alshifa-2026.11.21.1.zip' | Out-Null
        $signed = Assert-UpdateManifest ([IO.File]::ReadAllBytes((Join-Path $published 'manifest.json'))) ([Convert]::FromBase64String([IO.File]::ReadAllText((Join-Path $published 'manifest.json.sig')))) $public
        if ((Get-FileHash -LiteralPath (Join-Path $published 'alshifa-2026.11.21.1.zip') -Algorithm SHA256).Hash -ne $signed.sha256) { throw 'Published package does not match signed manifest.' }
        'PASS: publisher packaging, detached signature and actual archive hash agree.'
    } finally { Remove-Item -LiteralPath $privateKey -Force }
    $nightly = New-KabulBackupTrigger
    $boundary = [DateTimeOffset]::Parse($nightly.StartBoundary)
    if ($boundary.Hour -ne 2 -or $boundary.Minute -ne 0 -or $boundary.Offset -ne [TimeSpan]::FromMinutes(270)) { throw 'Nightly backup is not anchored to Kabul 02:00.' }
    'PASS: nightly schedule explicitly anchors to 02:00 Kabul independent of host DST.'
    $approval = Join-Path $work 'approval.json'
    Assert-Refused { Assert-UpdateApproval $approval '2026.11.21.1' } 'event without protected API approval'
    [IO.File]::WriteAllText($approval, (@{ version='2026.11.21.1'; approvedAt=[DateTimeOffset]::UtcNow.ToString('o') } | ConvertTo-Json))
    Assert-UpdateApproval $approval '2026.11.21.1'
    'PASS: protected fresh approval for exact version accepted.'
    Assert-Refused { Assert-UpdateApproval $approval '2026.11.22.1' } 'approval for another version'
    [IO.File]::WriteAllText($approval, (@{ version='2026.11.21.1'; approvedAt=[DateTimeOffset]::UtcNow.AddMinutes(-11).ToString('o') } | ConvertTo-Json))
    Assert-Refused { Assert-UpdateApproval $approval '2026.11.21.1' } 'expired approval'
    'PASS: update trust and package safety tests. Actual Windows installation/rollback is a separate acceptance test.'
} finally { $rsa.Dispose() }
