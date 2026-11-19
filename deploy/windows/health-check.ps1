# One-page status of the hospital host. Safe to run at any time; it changes nothing. Exit code 1 means something needs attention.
param(
    [string]$Root = 'C:\AlShifa',
    # Without it, the local address this installation was given is read from its settings. The installer may have chosen another port.
    [string]$HealthUrl
)

$ErrorActionPreference = 'Stop'
$problems = 0
if (-not $HealthUrl) {
    try {
        $HealthUrl = "$((Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json).Kestrel.Endpoints.Loopback.Url)/health"
    } catch {
        $HealthUrl = 'http://127.0.0.1:5080/health'
    }
}
function Report([string]$Name, [bool]$Ok, [string]$Detail) {
    '{0,-6} {1,-22} {2}' -f $(if ($Ok) { 'OK' } else { 'FAIL' }), $Name, $Detail
    if (-not $Ok) { $script:problems++ }
}

foreach ($name in 'AlShifaMySQL', 'AlShifaApi') {
    $service = Get-Service $name -ErrorAction SilentlyContinue
    Report "Service $name" ($service.Status -eq 'Running') $(if ($service) { "$($service.Status), start: $($service.StartType)" } else { 'not installed' })
}

try {
    $response = Invoke-WebRequest $HealthUrl -UseBasicParsing -TimeoutSec 10
    Report 'API and database' ($response.Content -eq 'Healthy') $response.Content
} catch {
    Report 'API and database' $false "$_"
}

$drive = Get-PSDrive ([IO.Path]::GetPathRoot($Root).Substring(0, 1))
$freeGb = [Math]::Round($drive.Free / 1GB, 1)
Report 'Disk space' ($freeGb -ge 10) "$freeGb GB free"

try {
    $status = Get-Content (Join-Path $Root 'logs\backup-status.json') -Raw | ConvertFrom-Json
    $age = if ($status.lastSuccessAt) { ((Get-Date) - [DateTime]$status.lastSuccessAt).TotalHours } else { [double]::MaxValue }
    Report 'Last backup' ($age -le 24 -and -not $status.lastError) "last success: $($status.lastSuccessAt)  error: $($status.lastError)"
} catch {
    Report 'Last backup' $false 'no backup status found'
}

try {
    $certificateConfig = Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json
    $password = $certificateConfig.Kestrel.Endpoints.Lan.Certificate.Password
    $certificatePath = $certificateConfig.Kestrel.Endpoints.Lan.Certificate.Path
    # EphemeralKeySet: only the expiry date is needed, so the private key is not written to a key store.
    $certificate = New-Object Security.Cryptography.X509Certificates.X509Certificate2($certificatePath, $password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    $minimumDays = if ($certificateConfig.LanAccess.Mode -eq 'PublicDns01') { 14 } else { 60 }
    try { Report 'HTTPS certificate' ($certificate.NotAfter -gt (Get-Date).AddDays($minimumDays)) "valid until $($certificate.NotAfter.ToString('yyyy-MM-dd'))" }
    finally { $certificate.Dispose() }
} catch {
    Report 'HTTPS certificate' $false 'could not read the certificate (run as Administrator)'
}

$appConfig = Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw -ErrorAction SilentlyContinue | ConvertFrom-Json -ErrorAction SilentlyContinue
if ($appConfig.LanAccess.Mode -eq 'PublicDns01') {
    try {
        & (Join-Path $PSScriptRoot 'test-lan-access.ps1') -Root $Root
        Report 'Trusted LAN' $true $appConfig.LanAccess.Url
        $renewal = Get-ScheduledTask -TaskName 'AlShifa Certificate Renewal' -ErrorAction Stop
        $renewalInfo = Get-ScheduledTaskInfo -TaskName 'AlShifa Certificate Renewal'
        Report 'Certificate renewal' ($renewal.State -ne 'Disabled' -and ($renewal.State -eq 'Running' -or $renewalInfo.LastTaskResult -eq 0)) "daily/startup; last result: $($renewalInfo.LastTaskResult)"
    } catch { Report 'Trusted LAN' $false "$_" }
}
"INFO   Online copy            $(if ($appConfig.Sync.Url) { "sync to $($appConfig.Sync.Url) (status in Settings > Backup; never needed for hospital work)" } else { 'not set up (online.ps1)' })"
if (Get-Service Cloudflared -ErrorAction SilentlyContinue) { "INFO   Old remote access      a Cloudflared service is installed; online.ps1 removes the one this system set up" }

if ($problems) { exit 1 }
