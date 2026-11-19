# Default: prepare only. -Activate configures the installed server after its LAN DNS is ready.
[CmdletBinding(DefaultParameterSetName='Prepare')]
param(
    [string]$HostName = 'lan-shafa.novatechsoft.com',
    [Parameter(Mandatory)][string]$LanIp,
    [Parameter(Mandatory,ParameterSetName='Prepare')][string]$OutputDirectory,
    [ValidateRange(1024,65535)][int]$LoopbackPort = 5080,
    [ValidateRange(1,65535)][int]$Port = 443,
    [string]$OnlineUrl = 'https://shafa.novatechsoft.com',
    [Parameter(Mandatory,ParameterSetName='Activate')][switch]$Activate,
    [Parameter(Mandatory,ParameterSetName='Activate')][string]$Email,
    [Parameter(ParameterSetName='Activate')][securestring]$CloudflareToken,
    [Parameter(ParameterSetName='Activate')][switch]$AcceptAcmeTerms,
    [Parameter(ParameterSetName='Activate')][string]$ModuleArchive,
    [string]$Root = 'C:\AlShifa'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'trusted-lan-common.ps1')
Assert-TrustedLanName $HostName $OnlineUrl
Assert-PrivateLanIp $LanIp
$url = "https://$HostName$(if ($Port -ne 443) { ":$Port" })"
if (-not $Activate) {
    if (Test-Path -LiteralPath $OutputDirectory) { throw 'Choose a new output directory; existing files are preserved.' }
    New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
    Write-AtomicLanJson (Join-Path $OutputDirectory 'network-plan.json') ([ordered]@{
        HostName = $HostName; LanIp = $LanIp; Url = "$url/login"; OnlineUrl = $OnlineUrl
        DhcpReservation = 'Reserve this server MAC address to LanIp on the hospital router.'
        InternalDns = "Create a LAN-only A record: $HostName -> $LanIp; advertise that DNS resolver by DHCP."
        Cloudflare = "Zone novatechsoft.com: scoped token with Zone DNS Edit and Zone Read; DNS-01 creates only _acme-challenge.$HostName TXT. No public A record or WAN port forwarding required."
        Wifi = 'Staff Wi-Fi must reach the LAN server; guest/client isolation must not block authorized staff.'
    })
    $guide = @"
Local system: $url/login
Online and sync remain: $OnlineUrl

1. On the router reserve $LanIp for the Windows Server network-card MAC address.
2. In the router/Windows internal DNS add $HostName -> $LanIp. DHCP clients must use that DNS during WAN outages too.
   Do NOT change public shafa.novatechsoft.com. Do NOT use per-device hosts files.
3. Install AlShifa on the server once with the existing installer. Staff need no installer or certificate files.
4. Run prepare-trusted-lan.ps1 -Activate -HostName $HostName -LanIp $LanIp -Port $Port -Email <certificate-contact-email> -AcceptAcmeTerms
   Run as Administrator. It securely prompts for the Cloudflare token, issues the certificate under SYSTEM,
   adds daily/startup renewal and changes only Kestrel's LAN endpoint. Existing Sync/database settings are preserved.
5. Run test-lan-access.ps1 -Root C:\AlShifa; verify the address from an unmodified phone too.
6. Staff open $url/login using hospital Wi-Fi and choose Install / Add to Home Screen in Edge, Chrome or Safari.
7. Disconnect external internet and verify local login/work. Allow server internet periodically for renewal before expiry.

Only the server needs outbound HTTPS to Cloudflare/Let's Encrypt and initial Windows public-root updates.
Use a zone-scoped token, not the Cloudflare global API key. No secret goes to staff devices.
Windows DNS alternative (DNS role already installed): primary zone $HostName with A record @ -> $LanIp;
then advertise that DNS server through router DHCP. Review existing records rather than overwriting them.
Android Private DNS / browser Secure DNS must not bypass LAN DNS. No certificate installation is needed.
"@
    [IO.File]::WriteAllText((Join-Path $OutputDirectory 'SERVER-SETUP.txt'), $guide, (New-Object Text.UTF8Encoding($false)))
    Write-Host "Prepared network plan: $OutputDirectory"
    return
}
. (Join-Path $PSScriptRoot 'common.ps1')
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as Administrator on the installed server.' }
if (-not $AcceptAcmeTerms) { throw 'Review the ACME subscriber agreement, then use -AcceptAcmeTerms.' }
if ($Email -notmatch '^[^\s@]+@[^\s@]+\.[^\s@]+$') { throw 'A certificate contact email is required.' }
if ($Root -match '["\r\n]') { throw 'Invalid installation path.' }
$configPath = Join-Path $Root 'app\appsettings.Production.json'
if (-not (Test-Path -LiteralPath $configPath) -or -not (Get-Service AlShifaApi -ErrorAction SilentlyContinue)) { throw 'Install the AlShifa server first.' }
$serviceIdentity = Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
if ($serviceIdentity.PathName.Trim().Trim('"') -ne (Join-Path $Root 'app\AlShifa.Api.exe')) { throw 'Root does not match the installed AlShifaApi service. No configuration was changed.' }
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.Mirror.Enabled) { throw 'This is an online mirror, not the hospital LAN server.' }
Assert-TrustedLanName $HostName $config.Sync.Url
if (-not (Get-NetIPAddress -AddressFamily IPv4 -IPAddress $LanIp -ErrorAction SilentlyContinue)) { throw 'LanIp is not assigned to this server. Reserve its actual address in DHCP first.' }
$addresses = @([Net.Dns]::GetHostAddresses($HostName) | ForEach-Object IPAddressToString)
if ($addresses -notcontains $LanIp -or @($addresses | Where-Object { $_ -ne $LanIp }).Count) { throw "Internal DNS must resolve $HostName only to $LanIp before activation. Configure router DNS/DHCP first." }
$oldPort = ([uri]$config.Kestrel.Endpoints.Lan.Url).Port
if ($Port -ne $oldPort -and (Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)) { throw "Port $Port is occupied. Nothing was replaced." }
$taskName = 'AlShifa Certificate Renewal'
$existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
$installedScript = Join-Path $Root 'scripts\renew-trusted-lan.ps1'
if ($existingTask -and ($existingTask.Actions.Arguments -notlike "*`"$installedScript`"*" -or $existingTask.State -eq 'Running')) { throw 'The certificate task is not owned by this installation, or is currently running.' }
$tlsRoot = Join-Path $Root 'tls'
New-Item -ItemType Directory -Path $tlsRoot -Force | Out-Null
Protect-Path $tlsRoot
$pin = (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerequisites.json') -Raw | ConvertFrom-Json).items | Where-Object id -eq 'posh-acme'
if (-not $pin) { throw 'The pinned ACME prerequisite is missing.' }
if (-not $ModuleArchive) {
    $bundled = Join-Path (Split-Path $PSScriptRoot -Parent) "prerequisites\$($pin.file)"
    if (Test-Path -LiteralPath $bundled) { $ModuleArchive = $bundled }
    else {
        $ModuleArchive = Join-Path $tlsRoot $pin.file
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        if (-not (Test-Path -LiteralPath $ModuleArchive)) { Invoke-WebRequest $pin.url -UseBasicParsing -OutFile $ModuleArchive }
    }
}
if ((Get-FileHash -LiteralPath $ModuleArchive -Algorithm SHA256).Hash -ne $pin.sha256) { throw 'ACME module hash does not match the pinned package. It was not loaded.' }
$modulePath = Join-Path $tlsRoot "modules\Posh-ACME\$($pin.version)"
if (-not (Test-Path -LiteralPath $modulePath)) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($ModuleArchive, $modulePath)
}
foreach ($file in 'renew-trusted-lan.ps1','trusted-lan-common.ps1','test-lan-access.ps1','prerequisites.json') {
    $source = Join-Path $PSScriptRoot $file
    $target = Join-Path $Root "scripts\$file"
    if ([IO.Path]::GetFullPath($source) -ne [IO.Path]::GetFullPath($target)) { Copy-Item -LiteralPath $source -Destination $target -Force }
}
if (-not $CloudflareToken -and $env:ALSHIFA_CLOUDFLARE_TOKEN) { $CloudflareToken = ConvertTo-SecureString $env:ALSHIFA_CLOUDFLARE_TOKEN -AsPlainText -Force }
Remove-Item Env:\ALSHIFA_CLOUDFLARE_TOKEN -ErrorAction SilentlyContinue
if (-not $CloudflareToken) { $CloudflareToken = Read-Host 'Cloudflare zone DNS Edit + Zone Read API token' -AsSecureString }
$tokenPlain = (New-Object Net.NetworkCredential('', $CloudflareToken)).Password
if (-not $tokenPlain) { throw 'The Cloudflare token is empty.' }
$password = $config.Kestrel.Endpoints.Lan.Certificate.Password
if (-not $password) { $password = New-Secret }
Write-AtomicLanJson (Join-Path $tlsRoot 'settings.json') ([ordered]@{
    HostName = $HostName.ToLowerInvariant(); LanIp = $LanIp; Port = $Port; Email = $Email
    Token = (Protect-LanSecret $tokenPlain); PfxPassword = (Protect-LanSecret $password)
})
$tokenPlain = $null
$password = $null
$shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$action = New-ScheduledTaskAction -Execute $shell -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$installedScript`" -Root `"$Root`""
$triggers = @((New-ScheduledTaskTrigger -Daily -At '03:15'), (New-ScheduledTaskTrigger -AtStartup))
$options = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Minutes 20) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 15)
$originalConfig = Get-Content -LiteralPath $configPath -Raw
$createdTask = -not $existingTask
$firewall = Get-NetFirewallRule -DisplayName 'Al-Shifa trusted HTTPS' -ErrorAction SilentlyContinue
try {
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $triggers -Settings $options -User SYSTEM -RunLevel Highest -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    $started = Get-Date
    do {
        Start-Sleep -Seconds 2
        $task = Get-ScheduledTask -TaskName $taskName
        $info = Get-ScheduledTaskInfo -TaskName $taskName
        if ((Get-Date) - $started -gt (New-TimeSpan -Minutes 18)) { throw 'Certificate task timed out.' }
    } while ($task.State -eq 'Running' -or $info.LastRunTime -lt $started.AddSeconds(-2))
    if ($info.LastTaskResult -ne 0) { throw "Certificate issuance failed. Check Cloudflare permissions, server internet and $tlsRoot\status.json; rerun after fixing it." }
    if (-not $firewall) {
        New-NetFirewallRule -DisplayName 'Al-Shifa trusted HTTPS' -Direction Inbound -Protocol TCP -LocalAddress $LanIp -LocalPort $Port -RemoteAddress LocalSubnet -Profile Any -Action Allow | Out-Null
    }
    & (Join-Path $Root 'scripts\test-lan-access.ps1') -Root $Root
    if (-not $?) { throw 'Trusted LAN verification failed.' }
    Get-NetFirewallRule -DisplayName 'Al-Shifa HTTPS (hospital LAN)' -ErrorAction SilentlyContinue | Disable-NetFirewallRule | Out-Null
    $shortcut = Get-AlShifaShortcut
    if ($shortcut) { Set-AlShifaShortcut $shortcut.Name "$url/login" (Join-Path $Root 'app\wwwroot\favicon.ico') }
    Write-Host "Ready for staff: $url/login"
    Write-Host 'Staff need only hospital Wi-Fi and their own login. No client files or certificate installation.'
} catch {
    if ($createdTask) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Write-AtomicLanJson $configPath ($originalConfig | ConvertFrom-Json)
        if (-not $firewall) { Get-NetFirewallRule -DisplayName 'Al-Shifa trusted HTTPS' -ErrorAction SilentlyContinue | Remove-NetFirewallRule }
    }
    throw
}
