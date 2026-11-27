# Trust anchor is provisioned once by an administrator. Never download it beside the manifest.
function Assert-UpdateApproval([string]$Path, [string]$Version) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'No protected API approval exists.' }
    $approval = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    $time = [DateTimeOffset]::Parse($approval.approvedAt).ToUniversalTime()
    $age = [DateTimeOffset]::UtcNow - $time
    if ($approval.version -ne $Version -or $age.TotalMinutes -gt 10 -or $age.TotalSeconds -lt -30) { throw 'API approval is stale or belongs to a different version.' }
}

function Assert-UpdateManifest([byte[]]$Bytes, [byte[]]$Signature, [string]$PublicKeyPath) {
    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    try {
        $rsa.FromXmlString([IO.File]::ReadAllText($PublicKeyPath))
        if ($rsa.KeySize -lt 3072 -or -not $rsa.VerifyData($Bytes, 'SHA256', $Signature)) { throw 'Update signature refused.' }
        $manifest = [Text.Encoding]::UTF8.GetString($Bytes) | ConvertFrom-Json
        if ($manifest.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $manifest.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or [long]$manifest.sizeBytes -le 0 -or [long]$manifest.sizeBytes -gt 2GB) { throw 'Invalid update manifest.' }
        return $manifest
    } finally { $rsa.Dispose() }
}

function Receive-UpdateFile([uri]$Uri, [string]$Path, [long]$MaximumBytes) {
    if ($Uri.Scheme -ne 'https' -or $Uri.UserInfo -or $Uri.Fragment) { throw 'Update downloads require HTTPS without embedded credentials.' }
    Add-Type -AssemblyName System.Net.Http
    $handler = New-Object Net.Http.HttpClientHandler
    $handler.AllowAutoRedirect = $false
    $client = New-Object Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(15)
    $deadline = New-Object Threading.CancellationTokenSource
    $deadline.CancelAfter([TimeSpan]::FromMinutes(15))
    try {
        $reply = $client.GetAsync($Uri, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $deadline.Token).GetAwaiter().GetResult()
        try {
            if ([int]$reply.StatusCode -ne 200) { throw 'Update download failed or redirected.' }
            if ($reply.Content.Headers.ContentLength -gt $MaximumBytes) { throw 'Update download exceeds size limit.' }
            $inputStream = $reply.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            $outputStream = [IO.File]::Create($Path)
            try {
                $buffer = New-Object byte[] 81920
                $total = 0L
                while (($count = $inputStream.ReadAsync($buffer, 0, $buffer.Length, $deadline.Token).GetAwaiter().GetResult()) -gt 0) {
                    $total += $count
                    if ($total -gt $MaximumBytes) { throw 'Update download exceeds size limit.' }
                    $outputStream.Write($buffer, 0, $count)
                }
            } finally { $outputStream.Dispose(); $inputStream.Dispose() }
        } finally { $reply.Dispose() }
    } finally { $deadline.Dispose(); $client.Dispose(); $handler.Dispose() }
}

function Assert-UpdateArchive([string]$Archive, [string]$Destination) {
    $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
    try {
        $prefix = [IO.Path]::GetFullPath($Destination).TrimEnd('\') + '\'
        $total = 0L
        $paths = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName.Replace('/', '\')
            if ($name -match '(^|\\)(secrets|backups|backup-staging|storage|data|keys)(\\|$)' -or $name -match '(^|\\)\.env($|\.)' -or $name -match '\.(cnf|pfx|p12)$') { throw 'Host data or secrets are prohibited in update archives.' }
            $full = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or $name.Contains(':') -or $name.StartsWith('\') -or -not $paths.Add($full)) { throw 'Unsafe update archive path.' }
            if ((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000) { throw 'Archive symlinks are prohibited.' }
            $total += $entry.Length
            if ($total -gt 4GB -or $zip.Entries.Count -gt 100000) { throw 'Update archive exceeds extraction limit.' }
        }
        foreach ($required in 'app/AlShifa.Api.exe', 'app/release.json', 'scripts/update.ps1') {
            if (-not ($zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq $required })) { throw 'Update archive is incomplete.' }
        }
        if ($zip.Entries | Where-Object { $_.FullName.Replace('\', '/') -eq 'app/appsettings.Production.json' }) { throw 'A release must not contain host secrets.' }
    } finally { $zip.Dispose() }
}

# Downloaded packages and unpacked copies of versions other than the one just installed. Program files only, never data.
function Remove-OldUpdateFiles([string]$Cache, [string]$KeepVersion) {
    Get-ChildItem -LiteralPath $Cache -Force -ErrorAction SilentlyContinue | Where-Object {
        ($_.Name -like 'alshifa-*.zip*' -and $_.Name -notlike "alshifa-$KeepVersion.zip") -or $_.Name -like 'release-*' -or $_.Name -like 'approval-consumed-*'
    } | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

# Enrolls the publisher feed shipped with a release (scripts\update-publisher.json), once, so later approved versions
# reach the hospital without anyone visiting it. An enrolled key is never replaced from a release.
function Enable-PublisherFeed([string]$Root, [string]$ScriptsDir, [string]$BackupDestination, [bool]$AllowSameDisk) {
    $publisherPath = Join-Path $ScriptsDir 'update-publisher.json'
    if (-not (Test-Path -LiteralPath $publisherPath)) { return 'Automatic updates: no publisher feed in this release; unchanged.' }
    $publisher = Get-Content -LiteralPath $publisherPath -Raw | ConvertFrom-Json
    $feed = [uri]$publisher.manifestUrl
    if ($feed.Scheme -ne 'https' -or $feed.Query -or $feed.Fragment -or $feed.UserInfo) { throw 'The publisher feed must be HTTPS without credentials.' }
    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    try {
        $rsa.FromXmlString([string]$publisher.publicKey)
        if ($rsa.KeySize -lt 3072 -or -not $rsa.PublicOnly) { throw 'The publisher key must be a public RSA key of at least 3072 bits.' }
        $publicXml = $rsa.ToXmlString($false)
    } finally { $rsa.Dispose() }
    $secrets = Join-Path $Root 'secrets'
    $keyPath = Join-Path $secrets 'update-public.xml'
    if (Test-Path -LiteralPath $keyPath) {
        if ([IO.File]::ReadAllText($keyPath) -ne $publicXml) { return 'Automatic updates: another publisher key is enrolled; unchanged.' }
    } else { [IO.File]::WriteAllText($keyPath, $publicXml) }
    $settingsPath = Join-Path $secrets 'auto-update.json'
    $settings = if (Test-Path -LiteralPath $settingsPath) { Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } else {
        [pscustomobject]@{ manifestUrl = $feed.AbsoluteUri; backupDestination = $BackupDestination; allowSameDisk = $AllowSameDisk }
    }
    # The hospital requests installation by a button; migrate the old unattended
    # night policy without changing the enrolled URL, trust key or backup target.
    $settings | Add-Member -NotePropertyName autoInstall -NotePropertyValue $false -Force
    [IO.File]::WriteAllText(($settingsPath + '.tmp'), ($settings | ConvertTo-Json))
    Move-Item -LiteralPath ($settingsPath + '.tmp') -Destination $settingsPath -Force
    return 'Automatic update detection enabled; installation requires approval for the exact verified version.'
}
