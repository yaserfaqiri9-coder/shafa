# One-time technical administrator enrollment. Routine users see only update status and an approval button.
#Requires -RunAsAdministrator
param([Parameter(Mandatory)][uri]$ManifestUrl, [Parameter(Mandatory)][string]$PublisherPublicKey,
    [Parameter(Mandatory)][string]$BackupDestination, [string]$Root = 'C:\AlShifa', [switch]$AllowSameDisk, [switch]$AutoInstall)
. (Join-Path $PSScriptRoot 'common.ps1')
if ($AutoInstall) { throw 'Unattended installation is disabled. Updates are installed after the hospital administrator presses Install.' }
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
$service = Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
if (-not $service -or $service.PathName.Trim().Trim('"') -ne (Join-Path $Root 'app\AlShifa.Api.exe')) { throw 'Root does not match the installed hospital API service.' }
if ($ManifestUrl.Scheme -ne 'https' -or $ManifestUrl.Query -or $ManifestUrl.Fragment -or $ManifestUrl.UserInfo) { throw 'A public HTTPS publisher manifest without credentials is required.' }
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
try {
    $rsa.FromXmlString([IO.File]::ReadAllText((Resolve-Path -LiteralPath $PublisherPublicKey).Path))
    if ($rsa.KeySize -lt 3072 -or -not $rsa.PublicOnly) { throw 'Supply only the publisher public RSA key, at least 3072 bits.' }
    $publicXml = $rsa.ToXmlString($false)
} finally { $rsa.Dispose() }
$secrets = Join-Path $Root 'secrets'
$keyPath = Join-Path $secrets 'update-public.xml'
if (Test-Path -LiteralPath $keyPath) {
    if ([IO.File]::ReadAllText($keyPath) -ne $publicXml) { throw 'Publisher key rotation requires a separately reviewed trust-anchor change.' }
} else { [IO.File]::WriteAllText($keyPath, $publicXml) }
Protect-Path $secrets
$settings = @{ manifestUrl = $ManifestUrl.AbsoluteUri; backupDestination = $BackupDestination; allowSameDisk = [bool]$AllowSameDisk; autoInstall = [bool]$AutoInstall }
$path = Join-Path $secrets 'auto-update.json'
[IO.File]::WriteAllText(($path + '.tmp'), ($settings | ConvertTo-Json))
Move-Item -LiteralPath ($path + '.tmp') -Destination $path -Force
Set-AutoUpdateTasks $Root
Start-ScheduledTask -TaskName 'AlShifa Update Check'
'Publisher enrolled. New versions are checked every minute; installation requires approval for the verified version.'
