# Run on the development computer. Produces release\alshifa-<date>\ to copy to the hospital host.
# Self-contained: the host needs no .NET or Node installation. Contains no secrets and no patient data.
param([string]$OutputRoot, [string]$Version = (Get-Date).ToUniversalTime().ToString('yyyy.MM.dd.HHmm'))

. (Join-Path $PSScriptRoot 'common.ps1')

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'deploy\verify-voice-assets.ps1')
Assert-RecordedVoiceAssets $repo | Out-Null
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'release' }
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$target = Join-Path $OutputRoot "alshifa-$(Get-Date -Format 'yyyyMMdd-HHmm')"
$app = Join-Path $target 'app'

Push-Location (Join-Path $repo 'frontend')
$oldVersion=$env:VITE_RELEASE_VERSION
try {
    $env:VITE_RELEASE_VERSION=$Version
    Invoke-Native 'cmd.exe' @('/c', 'npm', 'run', 'build') | Out-Null
} finally {
    $env:VITE_RELEASE_VERSION=$oldVersion
    Pop-Location
}

$project = Join-Path $repo 'backend\src\AlShifa.Api\AlShifa.Api.csproj'
Invoke-Native $dotnet @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $app, '--nologo', '-v', 'q') | Out-Null

Copy-ReleaseWebFiles (Join-Path $repo 'frontend\dist') (Join-Path $app 'wwwroot')
# The database version of this build: the online copy must accept it before the release is offered to the hospital.
$schema = (Get-ChildItem (Join-Path $repo 'backend\src\AlShifa.Api\Infrastructure\Migrations') -Filter '2*.cs' |
    Where-Object { $_.Name -notlike '*.Designer.cs' } | Sort-Object Name | Select-Object -Last 1).BaseName
[IO.File]::WriteAllText((Join-Path $app 'release.json'), (@{ version = $Version; schema = $schema } | ConvertTo-Json))
Copy-Item $PSScriptRoot (Join-Path $target 'scripts') -Recurse
foreach ($guide in 'INSTALL-SIMPLE.md', 'PILOT-CHECKLIST.md', 'DEPLOYMENT-WINDOWS.md') { Copy-Item (Join-Path $repo $guide) $target }
"Release ready: $target"
