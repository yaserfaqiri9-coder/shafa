# Shared by preparation, activation, renewal and the read-only installation check.
function Assert-TrustedLanName([string]$HostName, [string]$OnlineUrl) {
    if ($HostName -notmatch '^(?=.{1,253}$)([a-zA-Z0-9](?:[a-zA-Z0-9-]{0,61}[a-zA-Z0-9])?\.)+[a-zA-Z]{2,63}$' -or $HostName -match '\.(lan|local|internal|test|invalid|localhost)$') {
        throw 'A hostname under the hospital-owned public domain is required.'
    }
    if ($OnlineUrl -and ([uri]$OnlineUrl).DnsSafeHost -eq $HostName) {
        throw 'The LAN hostname must differ from Sync:Url. Internal DNS must not redirect online sync back to this server.'
    }
}

function Assert-PrivateLanIp([string]$LanIp) {
    $parsedAddress = $null
    if (-not [Net.IPAddress]::TryParse($LanIp, [ref]$parsedAddress) -or $parsedAddress.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $LanIp -notmatch '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)') {
        throw 'Use the reserved private IPv4 address of the hospital server.'
    }
}

function Write-AtomicLanJson([string]$Path, $Value) {
    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 40), (New-Object Text.UTF8Encoding($false)))
        if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temporary, $Path, "$Path.last-good", $true) }
        else { [IO.File]::Move($temporary, $Path) }
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}

function Get-TrustedLanConfig($Config, [string]$HostName, [string]$LanIp, [int]$Port, [string]$PfxPath, [string]$Password) {
    Assert-TrustedLanName $HostName $Config.Sync.Url
    Assert-PrivateLanIp $LanIp
    # Edit only the existing LAN endpoint. Preserve Sync, storage, database credentials and all other settings.
    $lanEndpoint = [pscustomobject]@{
        Url = "https://${LanIp}:$Port"
        SslProtocols = @('Tls12', 'Tls13')
        Certificate = [pscustomobject]@{ Path = $PfxPath; Password = $Password }
    }
    $Config.Kestrel.Endpoints | Add-Member -NotePropertyName Lan -NotePropertyValue $lanEndpoint -Force
    $Config | Add-Member -NotePropertyName LanAccess -NotePropertyValue ([pscustomobject]@{
        Mode = 'PublicDns01'; HostName = $HostName; LanIp = $LanIp; Port = $Port
        Url = "https://$HostName$(if ($Port -ne 443) { ":$Port" })"
    }) -Force
    return $Config
}

function Assert-LanServerCertificate([string]$PfxPath, [string]$Password, [string]$HostName) {
    $collection = New-Object Security.Cryptography.X509Certificates.X509Certificate2Collection
    $collection.Import($PfxPath, $Password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        $leaf = @($collection | Where-Object HasPrivateKey)
        if ($leaf.Count -ne 1 -or $leaf[0].GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::DnsName, $false) -ne $HostName -or $leaf[0].NotAfter -lt (Get-Date).AddDays(7) -or $leaf[0].NotBefore -gt (Get-Date)) {
            throw 'The certificate must contain this exact hostname, its private key and at least seven valid days.'
        }
        $chain = New-Object Security.Cryptography.X509Certificates.X509Chain
        try {
            $chain.ChainPolicy.RevocationMode = [Security.Cryptography.X509Certificates.X509RevocationMode]::NoCheck
            $chain.ChainPolicy.ExtraStore.AddRange($collection)
            if (-not $chain.Build($leaf[0])) { throw 'Certificate chain is not publicly trusted by Windows. A staging or private-CA certificate cannot be activated.' }
        } finally { $chain.Dispose() }
        return [pscustomobject]@{ Thumbprint = $leaf[0].Thumbprint; ExpiresAt = $leaf[0].NotAfter.ToUniversalTime().ToString('o') }
    } finally { foreach ($item in $collection) { $item.Dispose() } }
}

function Protect-LanSecret([string]$Plain) {
    Add-Type -AssemblyName System.Security
    [Convert]::ToBase64String([Security.Cryptography.ProtectedData]::Protect([Text.Encoding]::UTF8.GetBytes($Plain), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine))
}

function Unprotect-LanSecret([string]$Encrypted) {
    Add-Type -AssemblyName System.Security
    [Text.Encoding]::UTF8.GetString([Security.Cryptography.ProtectedData]::Unprotect([Convert]::FromBase64String($Encrypted), $null, [Security.Cryptography.DataProtectionScope]::LocalMachine))
}
