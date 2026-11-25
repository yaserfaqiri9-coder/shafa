# Publisher only, on the development computer. Signs a release built by build-release.ps1 and offers it to the
# hospital's automatic updater through the online server. Run AFTER the same build is deployed online
# (deploy-online-release.sh); the server refuses a release whose database version differs from the online copy.
#   .\publish-update.ps1 -Release ..\..\release\alshifa-20261126-1200 -Server root@<online host> -SshKey ~\.ssh\<key>
# The private key never leaves this computer. Keep a copy of it offline: without it no further update can be signed.
param(
    [Parameter(Mandatory)][string]$Release,
    [Parameter(Mandatory)][string]$Server,
    [Parameter(Mandatory)][string]$SshKey,
    [string]$PrivateKeyPath = (Join-Path $env:USERPROFILE '.alshifa-publisher\update-private.xml'),
    [switch]$CheckOnly
)
. (Join-Path $PSScriptRoot 'common.ps1')
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$marker = Get-Content -LiteralPath (Join-Path $Release 'app\release.json') -Raw | ConvertFrom-Json
if (-not $marker.schema) { throw 'Release marker has no database version; rebuild with build-release.ps1.' }
$publisher = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'update-publisher.json') -Raw | ConvertFrom-Json
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
try {
    $rsa.FromXmlString([IO.File]::ReadAllText($PrivateKeyPath))
    if ($rsa.ToXmlString($false) -ne $publisher.publicKey) { throw 'The private key does not match update-publisher.json; hospitals would refuse this release.' }
} finally { $rsa.Dispose() }

$feed = [uri]$publisher.manifestUrl
$packageUrl = New-Object Uri($feed, "alshifa-$($marker.version).zip")
$output = Join-Path $repo ".tools\update-publish\$($marker.version)"
& (Join-Path $PSScriptRoot 'sign-update.ps1') -Release $Release -PackageUrl $packageUrl.AbsoluteUri -PrivateKeyPath $PrivateKeyPath -Output $output
Remove-Item -LiteralPath (Join-Path $output 'validation-only') -Recurse -Force -ErrorAction SilentlyContinue
"Signed $($marker.version) (database $($marker.schema)) in $output"
if ($CheckOnly) { 'Check only: nothing was sent.'; exit 0 }

$remote = "/root/alshifa-update-$($marker.version)"
$ssh = @('-i', $SshKey, '-o', 'BatchMode=yes')
Invoke-Native 'ssh.exe' ($ssh + @($Server, "mkdir -p $remote")) | Out-Null
Invoke-Native 'scp.exe' ($ssh + @((Join-Path $output 'manifest.json'), (Join-Path $output 'manifest.json.sig'), (Join-Path $output "alshifa-$($marker.version).zip"), (Join-Path $repo 'deploy\linux\publish-hospital-update.sh'), "${Server}:$remote/")) | Out-Null
Invoke-Native 'ssh.exe' ($ssh + @($Server, "bash $remote/publish-hospital-update.sh $remote && rm -rf $remote"))
