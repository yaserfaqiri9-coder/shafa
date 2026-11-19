# Read-only: DNS, trusted HTTPS, PWA and the unauthenticated access barrier. Does not log in or write data.
param([string]$Root = 'C:\AlShifa')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'trusted-lan-common.ps1')
$config = Get-Content -LiteralPath (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json
$access = $config.LanAccess
if ($access.Mode -ne 'PublicDns01') { throw 'Trusted LAN access is not activated.' }
Assert-TrustedLanName $access.HostName $config.Sync.Url
Assert-PrivateLanIp $access.LanIp
$addresses = @([Net.Dns]::GetHostAddresses($access.HostName) | ForEach-Object IPAddressToString)
if ($addresses -notcontains $access.LanIp -or @($addresses | Where-Object { $_ -ne $access.LanIp }).Count) { throw 'LAN DNS does not resolve exclusively to the reserved server IP.' }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$base = $access.Url.TrimEnd('/')
# Normal Windows trust and hostname checks stay enabled. Never install a root or bypass TLS validation.
$health = Invoke-WebRequest "$base/health" -UseBasicParsing -TimeoutSec 15
if ($health.Content.Trim() -ne 'Healthy') { throw 'API/database health failed.' }
$login = Invoke-WebRequest "$base/login" -UseBasicParsing -TimeoutSec 15
if ($login.StatusCode -ne 200 -or $login.Content -notmatch 'id="app"') { throw 'Login application did not load.' }
$manifest = (Invoke-WebRequest "$base/manifest.webmanifest" -UseBasicParsing -TimeoutSec 15).Content | ConvertFrom-Json
if ($manifest.start_url -ne '/login' -or $manifest.display -ne 'standalone') { throw 'Install the current PWA release: its icon must open /login in standalone mode.' }
foreach ($icon in $manifest.icons) {
    if ((Invoke-WebRequest "$base$($icon.src)" -UseBasicParsing -TimeoutSec 15).StatusCode -ne 200) { throw 'A PWA icon is missing.' }
}
$sw = Invoke-WebRequest "$base/sw.js" -UseBasicParsing -TimeoutSec 15
if ($sw.StatusCode -ne 200) { throw 'PWA service worker is missing.' }
$refused = $false
try { Invoke-WebRequest "$base/api/auth/me" -UseBasicParsing -TimeoutSec 15 | Out-Null }
catch { if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 401) { $refused = $true } else { throw } }
if (-not $refused) { throw 'An unauthenticated request was not refused.' }
Write-Output "PASS: LAN DNS, publicly trusted HTTPS, API/database, login page, PWA and authentication barrier: $base/login"
Write-Output 'On an unmodified phone verify the same URL, a real user login and LAN work with router WAN disconnected.'
