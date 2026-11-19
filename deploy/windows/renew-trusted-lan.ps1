# Runs only on the installed server, under SYSTEM. No database access and no global certificate trust changes.
param([string]$Root = 'C:\AlShifa')
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'trusted-lan-common.ps1')
$tlsRoot = Join-Path $Root 'tls'
$statusPath = Join-Path $tlsRoot 'status.json'
$tokenPlain = $null
$pfxPassword = $null
$mutex = New-Object Threading.Mutex($false, 'Global\AlShifaCertificateRenewal')
$locked = $false
$originalConfig = $null
$configChanged = $false
try {
    try { $locked = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $locked = $true }
    if (-not $locked) { exit 0 }
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne 'S-1-5-18') { throw 'Certificate renewal must run as SYSTEM through its scheduled task.' }
    $settings = Get-Content -LiteralPath (Join-Path $tlsRoot 'settings.json') -Raw | ConvertFrom-Json
    $tokenPlain = Unprotect-LanSecret $settings.Token
    $pfxPassword = Unprotect-LanSecret $settings.PfxPassword
    $env:POSHACME_HOME = Join-Path $tlsRoot 'acme'
    $pin = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerequisites.json') -Raw | ConvertFrom-Json).items | Where-Object id -eq 'posh-acme'
    Import-Module (Join-Path $tlsRoot "modules\Posh-ACME\$($pin.version)\Posh-ACME.psd1") -Force
    Set-PAServer LE_PROD | Out-Null
    $pluginArgs = @{ CFToken = (ConvertTo-SecureString $tokenPlain -AsPlainText -Force) }
    $orderName = 'alshifa-lan'
    $order = Get-PAOrder -Name $orderName
    if (-not $order -or $order.status -ne 'valid') {
        New-PACertificate -Domain $settings.HostName -Name $orderName -Contact $settings.Email -AcceptTOS `
            -Plugin Cloudflare -PluginArgs $pluginArgs -PfxPassSecure (ConvertTo-SecureString $pfxPassword -AsPlainText -Force) | Out-Null
    } else {
        Submit-Renewal -Name $orderName -PluginArgs $pluginArgs | Out-Null
    }
    $cert = Get-PACertificate -Name $orderName
    if (-not $cert.PfxFullChain -or -not (Test-Path -LiteralPath $cert.PfxFullChain)) { throw 'ACME did not produce a full-chain PFX.' }
    $validated = Assert-LanServerCertificate $cert.PfxFullChain $pfxPassword $settings.HostName
    $configPath = Join-Path $Root 'app\appsettings.Production.json'
    $originalConfig = Get-Content -LiteralPath $configPath -Raw
    $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
    $destination = Join-Path $Root "certs\lan-$($validated.Thumbprint).pfx"
    if ($config.Kestrel.Endpoints.Lan.Certificate.Path -ne $destination -or $config.LanAccess.HostName -ne $settings.HostName) {
        Copy-Item -LiteralPath $cert.PfxFullChain -Destination $destination -Force
        Protect-Path $destination @('NT SERVICE\AlShifaApi=R')
        $config = Get-TrustedLanConfig $config $settings.HostName $settings.LanIp $settings.Port $destination $pfxPassword
        if (-not (Test-Path -LiteralPath "$configPath.before-trusted-lan")) { Copy-Item -LiteralPath $configPath -Destination "$configPath.before-trusted-lan" }
        # A new certificate path changes the endpoint configuration; Kestrel reloads it without an API restart.
        Write-AtomicLanJson $configPath $config
        $configChanged = $true
        $served = $false
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        foreach ($attempt in 1..20) {
            try {
                $answer = Invoke-WebRequest "$($config.LanAccess.Url)/manifest.webmanifest" -UseBasicParsing -TimeoutSec 5
                if ($answer.StatusCode -eq 200) { $served = $true; break }
            } catch { Start-Sleep -Seconds 1 }
        }
        if (-not $served) { throw 'Kestrel did not serve the renewed trusted certificate.' }
    }
    Write-AtomicLanJson $statusPath ([ordered]@{ LastSuccessAt = [DateTime]::UtcNow.ToString('o'); ExpiresAt = $validated.ExpiresAt; Thumbprint = $validated.Thumbprint; LastError = $null })
} catch {
    if ($configChanged -and $originalConfig) { Write-AtomicLanJson $configPath ($originalConfig | ConvertFrom-Json) }
    # Credentials and full provider exceptions never reach the task output or public health endpoint.
    Write-AtomicLanJson $statusPath ([ordered]@{ LastAttemptAt = [DateTime]::UtcNow.ToString('o'); LastError = 'Certificate issuance/renewal failed. Check server internet, Cloudflare token permissions and the protected ACME state.' })
    exit 1
} finally {
    $tokenPlain = $null
    $pfxPassword = $null
    if ($locked) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
