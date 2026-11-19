# Isolated filesystem/certificate contract checks. No services, NIC/DNS changes, trust-store changes or database access.
param([string]$OpenSsl = 'C:\Program Files\Git\usr\bin\openssl.exe')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\trusted-lan-common.ps1')
. (Join-Path $repo 'deploy\windows\common.ps1')
$testRoot = Join-Path $repo ('.artifacts\trusted-lan-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
$checks = 0
function Check([bool]$Condition, [string]$Name) {
    if (-not $Condition) { throw "FAIL: $Name" }
    $script:checks++
    Write-Output "PASS: $Name"
}
$config = '{"Sync":{"Url":"https://shafa.novatechsoft.com","Key":"test-sync-key","IntervalMinutes":5},"ConnectionStrings":{"Default":"unchanged-test-database"},"Storage":{"Path":"D:\\storage"},"Kestrel":{"Endpoints":{"Lan":{"Url":"https://0.0.0.0:8443","Certificate":{"Path":"legacy.pfx","Password":"test"}},"Loopback":{"Url":"http://127.0.0.1:5080"}}}}' | ConvertFrom-Json
$new = Get-TrustedLanConfig $config 'lan-shafa.novatechsoft.com' '192.168.1.20' 443 'C:\AlShifa\certs\new.pfx' 'test-pfx-password'
Check ($new.Sync.Key -eq 'test-sync-key' -and $new.Sync.IntervalMinutes -eq 5 -and $new.Sync.Url -eq 'https://shafa.novatechsoft.com') 'Two-way sync configuration survives TLS changes'
Check ($new.ConnectionStrings.Default -eq 'unchanged-test-database' -and $new.Storage.Path -eq 'D:\storage' -and $new.Kestrel.Endpoints.Loopback.Url -eq 'http://127.0.0.1:5080') 'Database, storage and loopback remain unchanged'
Check ($new.LanAccess.Url -eq 'https://lan-shafa.novatechsoft.com' -and $new.Kestrel.Endpoints.Lan.Url -eq 'https://192.168.1.20:443') 'One fixed LAN origin binds only the private server interface'
$refused = $false
try { Assert-TrustedLanName 'shafa.novatechsoft.com' 'https://shafa.novatechsoft.com' } catch { $refused = $true }
Check $refused 'LAN DNS cannot redirect online sync to itself'
$refused = $false
try { Assert-PrivateLanIp '8.8.8.8' } catch { $refused = $true }
Check $refused 'Public IP rejected for the local listener'
$path = Join-Path $testRoot 'config.json'
Write-AtomicLanJson $path ([ordered]@{ generation = 1; Sync = 'unchanged' })
Write-AtomicLanJson $path ([ordered]@{ generation = 2; Sync = 'unchanged' })
Check (((Get-Content -LiteralPath $path -Raw | ConvertFrom-Json).generation -eq 2) -and ((Get-Content -LiteralPath "$path.last-good" -Raw | ConvertFrom-Json).generation -eq 1)) 'Atomic configuration write retains the previous version'
$sealed = Protect-LanSecret 'dummy-cloudflare-token'
Check ($sealed -notmatch 'dummy-cloudflare-token' -and (Unprotect-LanSecret $sealed) -ceq 'dummy-cloudflare-token') 'Machine-bound secret encryption round trip'
if (-not (Test-Path -LiteralPath $OpenSsl)) { throw 'OpenSSL is required for the untrusted-certificate refusal check.' }
Invoke-Native $OpenSsl @('req','-x509','-newkey','rsa:2048','-nodes','-keyout',(Join-Path $testRoot 'self.key'),'-out',(Join-Path $testRoot 'self.crt'),'-days','30','-subj','/CN=lan-shafa.novatechsoft.com','-addext','subjectAltName=DNS:lan-shafa.novatechsoft.com') | Out-Null
Invoke-Native $OpenSsl @('pkcs12','-export','-out',(Join-Path $testRoot 'self.pfx'),'-inkey',(Join-Path $testRoot 'self.key'),'-in',(Join-Path $testRoot 'self.crt'),'-passout','pass:test-password') | Out-Null
$refused = $false
try { Assert-LanServerCertificate (Join-Path $testRoot 'self.pfx') 'test-password' 'lan-shafa.novatechsoft.com' | Out-Null }
catch { $refused = $_.Exception.Message -like '*not publicly trusted*' }
Check $refused 'A self-signed certificate cannot be activated as trusted LAN HTTPS'
$plan = Join-Path $testRoot 'plan'
& (Join-Path $repo 'deploy\windows\prepare-trusted-lan.ps1') -LanIp '192.168.1.20' -OutputDirectory $plan
$profile = Get-Content -LiteralPath (Join-Path $plan 'network-plan.json') -Raw | ConvertFrom-Json
Check ($profile.HostName -eq 'lan-shafa.novatechsoft.com' -and $profile.Url -like '*/login' -and -not (Test-Path -LiteralPath (Join-Path $plan 'Caddyfile'))) 'Preparation creates only a LAN plan, without a second HTTPS stack'
Write-Output "$checks isolated trusted-LAN checks passed."
