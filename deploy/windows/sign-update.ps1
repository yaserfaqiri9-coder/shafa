# Publisher only. Keep the RSA private XML key offline; distribute only update-public.xml to installed hosts.
param([Parameter(Mandatory)][string]$Release, [Parameter(Mandatory)][string]$PackageUrl,
    [Parameter(Mandatory)][string]$PrivateKeyPath, [Parameter(Mandatory)][string]$Output)
. (Join-Path $PSScriptRoot 'common.ps1')
$releasePath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $Release).Path).TrimEnd('\') + '\'
$privatePath = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $PrivateKeyPath).Path)
if ($privatePath.StartsWith($releasePath, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFullPath($Output).StartsWith($releasePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Publisher private key and output must be outside the release folder.' }
$marker = Get-Content -LiteralPath (Join-Path $Release 'app\release.json') -Raw | ConvertFrom-Json
if ($marker.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or ([uri]$PackageUrl).Scheme -ne 'https') { throw 'Valid release marker and HTTPS package URL required.' }
New-Item -ItemType Directory -Force $Output | Out-Null
$package = Join-Path $Output ('alshifa-' + $marker.version + '.zip')
if (Test-Path -LiteralPath $package) { throw 'Publish to a new output directory; existing packages are immutable.' }
[IO.Compression.ZipFile]::CreateFromDirectory((Resolve-Path -LiteralPath $Release).Path, $package)
. (Join-Path $PSScriptRoot 'auto-update-common.ps1')
Assert-UpdateArchive $package (Join-Path $Output 'validation-only')
$manifest = [ordered]@{ version = $marker.version; schema = [string]$marker.schema; packageUrl = $PackageUrl; sha256 = (Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash; sizeBytes = (Get-Item -LiteralPath $package).Length; publishedAt = (Get-Date).ToUniversalTime().ToString('o') }
$bytes = [Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json))
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
try {
    $rsa.FromXmlString([IO.File]::ReadAllText($privatePath))
    if ($rsa.KeySize -lt 3072) { throw 'Publisher RSA key must be at least 3072 bits.' }
    [IO.File]::WriteAllBytes((Join-Path $Output 'manifest.json'), $bytes)
    [IO.File]::WriteAllText((Join-Path $Output 'manifest.json.sig'), [Convert]::ToBase64String($rsa.SignData($bytes, 'SHA256')))
} finally { $rsa.Dispose() }
'Signed update package and manifest created. No private key was included.'
