# Creates only public connection information for staff phones. Does not change services, certificates or databases.
param(
    [string]$Root = 'C:\AlShifa',
    [string]$LanIp,
    [string]$OutputDirectory,
    [switch]$NoOpen
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Security
$configPath = Join-Path $Root 'app\appsettings.Production.json'
$caPath = Join-Path $Root 'certs\alshifa-ca.cer'
if (-not (Test-Path -LiteralPath $configPath)) {
    throw 'Run this tool on the installed hospital server after Install-AlShifa.exe finishes. No installed server was found.'
}
$config = Get-Content -LiteralPath $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ($config.LanAccess.Mode -eq 'PublicDns01') {
    $trustedUrl = "$($config.LanAccess.Url.TrimEnd('/'))/login"
    Write-Host "Hospital address: $trustedUrl"
    Write-Host 'Staff open this address on hospital Wi-Fi and choose Install / Add to Home Screen. No files or certificates are required.'
    if (-not $NoOpen) { Start-Process $trustedUrl }
    return
}
if (-not (Test-Path -LiteralPath $caPath)) { throw 'This legacy installation has no CA certificate. Activate trusted LAN access instead.' }
$port = ([uri]$config.Kestrel.Endpoints.Lan.Url).Port
if (-not $LanIp) {
    $addresses = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
        $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' -and $_.AddressState -eq 'Preferred'
    } | Select-Object -ExpandProperty IPAddress -Unique)
    if ($addresses.Count -eq 1) { $LanIp = $addresses[0] }
    else {
        Write-Host ('Server network addresses: ' + ($addresses -join ', '))
        $LanIp = Read-Host 'Enter the hospital LAN IP shown by the installer (example: 192.168.1.20)'
    }
}
$parsed = $null
if (-not [Net.IPAddress]::TryParse($LanIp, [ref]$parsed) -or $parsed.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or [Net.IPAddress]::IsLoopback($parsed)) {
    throw 'Enter the IPv4 address of the hospital server, not localhost or a website.'
}
if (-not (Get-NetIPAddress -AddressFamily IPv4 -IPAddress $LanIp -ErrorAction SilentlyContinue)) {
    throw 'That IP is not assigned to this server. No connection package was created.'
}
$url = "https://$LanIp$(if ($port -ne 443) { ":$port" })/"
$ca = New-Object Security.Cryptography.X509Certificates.X509Certificate2($caPath)
try {
    if ($ca.HasPrivateKey) { throw 'The mobile package must contain a public CA certificate only.' }
    if ($ca.NotAfter -lt (Get-Date)) { throw 'The hospital CA certificate has expired. Ask the system administrator to renew it.' }
    $thumbprint = $ca.Thumbprint
    # Check the server certificate is actually valid for this IP. Never tell staff to bypass a certificate error.
    $pfx = $config.Kestrel.Endpoints.Lan.Certificate
    $pfxPath = [string]$pfx.Path
    if (-not [IO.Path]::IsPathRooted($pfxPath)) { $pfxPath = Join-Path (Join-Path $Root 'app') $pfxPath }
    $serverCert = New-Object Security.Cryptography.X509Certificates.X509Certificate2($pfxPath, [string]$pfx.Password, [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
    try {
        $san = $serverCert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' } | Select-Object -First 1
        # IP SANs are encoded as context-specific tag 7, length 4, then the IPv4 octets.
        $needle = [byte[]](@(0x87, 0x04) + $parsed.GetAddressBytes())
        $matchesIp = $false
        if ($san) {
            $bytes = $san.RawData
            for ($offset = 0; $offset -le $bytes.Length - $needle.Length; $offset++) {
                $same = $true
                for ($index = 0; $index -lt $needle.Length; $index++) {
                    if ($bytes[$offset + $index] -ne $needle[$index]) { $same = $false; break }
                }
                if ($same) { $matchesIp = $true; break }
            }
        }
        if (-not $matchesIp -or $serverCert.NotAfter -lt (Get-Date)) {
            throw 'The server certificate is expired or does not include this IP. Ask the administrator to fix the LAN address/certificate; do not bypass browser security.'
        }
    } finally { $serverCert.Dispose() }
} finally { $ca.Dispose() }
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path ([Environment]::GetFolderPath('Desktop')) ('AlShifa-Mobile-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'The output folder already exists. Choose a new folder; no files were overwritten.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
Copy-Item -LiteralPath $caPath -Destination (Join-Path $OutputDirectory 'alshifa-ca.cer')
$encodedCert = [Convert]::ToBase64String([IO.File]::ReadAllBytes($caPath))
$profileId = [guid]::NewGuid().ToString()
$certId = [guid]::NewGuid().ToString()
$profile = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>PayloadType</key><string>Configuration</string><key>PayloadVersion</key><integer>1</integer>
<key>PayloadIdentifier</key><string>com.alshifa.local-ca.$profileId</string><key>PayloadUUID</key><string>$profileId</string>
<key>PayloadDisplayName</key><string>Al-Shifa Hospital Local CA</string>
<key>PayloadDescription</key><string>Trust the hospital LAN certificate. Contains no private key, VPN or device management settings.</string>
<key>PayloadContent</key><array><dict>
<key>PayloadType</key><string>com.apple.security.root</string><key>PayloadVersion</key><integer>1</integer>
<key>PayloadIdentifier</key><string>com.alshifa.local-ca.certificate.$certId</string><key>PayloadUUID</key><string>$certId</string>
<key>PayloadDisplayName</key><string>Al-Shifa Hospital Local CA</string><key>PayloadContent</key><data>$encodedCert</data>
</dict></array></dict></plist>
"@
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'AlShifa-iPhone.mobileconfig'), $profile, (New-Object Text.UTF8Encoding($false)))
$html = @"
<!doctype html><html lang="fa" dir="rtl"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>اتصال موبایل به چشم‌الشفا</title>
<style>body{font:17px/1.9 Tahoma,Arial;background:#eff7f8;color:#29434a;margin:0}main{max-width:760px;margin:24px auto;padding:24px;background:white;border-radius:16px}a{color:#087e8b}li{margin:12px 0}code{direction:ltr;unicode-bidi:isolate;overflow-wrap:anywhere}.open{display:inline-block;background:#089bab;color:white;padding:12px 24px;border-radius:10px;text-decoration:none}h1{font-size:24px}h2{font-size:20px}</style>
<main><h1>اتصال موبایل به چشم‌الشفا</h1><p>آدرس مخصوص همین سرور شفاخانه:</p><p><a class="open" href="$url" dir="ltr">$url</a></p>
<p>موبایل و کمپیوتر سرور باید به شبکه شفاخانه وصل باشند. وای‌فای مهمان که دسترسی به دستگاه‌های داخلی را بسته است مناسب نیست. سرور باید روشن باشد؛ انترنت بیرونی برای کار داخل شبکه لازم نیست.</p>
<h2>یک بار: اعتماد به گواهی شبکه داخلی</h2><p>فقط فایل گواهی همین شفاخانه را از مسئول سیستم بگیرید. فایل PFX یا پوشه secrets نباید به موبایل منتقل شود.</p>
<p>اثر انگشت گواهی برای تطبیق توسط مسئول: <code>$thumbprint</code></p>
<h2>اندروید</h2><ol><li>فایل <a href="alshifa-ca.cer">alshifa-ca.cer</a> را با کیبل یا روش مورد اعتماد به موبایل انتقال دهید.</li><li>در تنظیمات امنیت موبایل، بخش گواهی‌ها، Install a certificate و CA certificate را انتخاب و همین فایل را نصب کنید. نام منو در برندهای مختلف فرق دارد.</li><li>آدرس بالا را در Chrome باز کنید؛ صفحه باید بدون هشدار گواهی باز شود.</li><li>با حساب خود وارد شوید. «نصب روی موبایل» یا از منوی Chrome، Install app / Add to Home screen را انتخاب کنید.</li></ol>
<h2>آیفون و آیپد</h2><ol><li>فایل <a href="AlShifa-iPhone.mobileconfig">AlShifa-iPhone.mobileconfig</a> را از مسئول سیستم دریافت و باز کنید؛ این پروفایل فقط گواهی عمومی شفاخانه را دارد.</li><li>در Settings → General → VPN &amp; Device Management پروفایل Al-Shifa Hospital Local CA را نصب کنید.</li><li>در Settings → General → About → Certificate Trust Settings اعتماد کامل همان گواهی را فعال کنید.</li><li>آدرس بالا را در Safari بدون هشدار باز کنید. از Share، گزینه Add to Home Screen و سپس Add را بزنید.</li></ol>
<h2>اگر باز نشد</h2><p>وای‌فای، روشن بودن سرور، IP و درگاه را بررسی کنید. از هشدار گواهی عبور نکنید؛ اگر IP عوض شده یا گواهی منقضی است، مسئول سیستم باید آن را تنظیم کند. IP سرور را در روتر ثابت یا رزرو کنید. نام alshifa.lan در موبایل بدون تنظیم DNS روتر الزاماً باز نمی‌شود؛ همین IP بالا را استفاده کنید.</p>
<p>این آدرس داخلی است. برای کار از بیرون باید مسیر امن جداگانه تنظیم شود. آیکن موبایل اجازه حساب را تغییر نمی‌دهد. درخواست آنلاین پس از تأیید سرور محلی قطعی می‌شود.</p>
<p><a href="https://support.apple.com/en-us/102390">راهنمای رسمی Apple</a> · <a href="https://support.google.com/pixelphone/answer/2844832">راهنمای رسمی Android</a></p></main></html>
"@
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'Open-Mobile-Guide.html'), $html, (New-Object Text.UTF8Encoding($true)))
[IO.File]::WriteAllText((Join-Path $OutputDirectory 'Hospital-Address.txt'), "$url`r`nCA SHA1 thumbprint: $thumbprint`r`nOnly public connection files are in this folder.`r`n")
Write-Host "Mobile connection folder ready: $OutputDirectory"
Write-Host "Hospital address: $url"
if (-not $NoOpen) { Start-Process (Join-Path $OutputDirectory 'Open-Mobile-Guide.html') }
