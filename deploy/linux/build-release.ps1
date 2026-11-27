# Run on the development computer. Builds the online copy for the Contabo server (Linux x64, self-contained):
# release\alshifa-linux-<commit>.tar.gz and its SHA-256, for deploy/linux/deploy-release.sh. Contains no secrets and no patient data.
# The same commit must be installed at the hospital (deploy/windows/update.ps1), or sync waits with "schema_mismatch".
param([string]$OutputRoot, [string]$Version = (Get-Date).ToUniversalTime().ToString('yyyy.MM.dd.HHmm'))

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\windows\common.ps1')

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'deploy\verify-voice-assets.ps1')
Assert-RecordedVoiceAssets $repo | Out-Null
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'release' }
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$commit = 'source-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
if (Test-Path -LiteralPath (Join-Path $repo '.git')) {
    $commit = (& git -C $repo rev-parse --short HEAD).Trim()
    if (& git -C $repo status --porcelain) { $commit += '-dirty-' + (Get-Date -Format 'yyyyMMdd-HHmmss') }
}
$work = Join-Path $OutputRoot "alshifa-linux-$commit"
if (Test-Path -LiteralPath $work) { throw "Release directory already exists: $work. Choose a new output directory." }

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
Invoke-Native $dotnet @('publish', $project, '-c', 'Release', '-r', 'linux-x64', '--self-contained', 'true', '-o', $work, '--nologo', '-v', 'q') | Out-Null
New-Item -ItemType Directory -Path (Join-Path $work 'wwwroot') -Force | Out-Null
Copy-ReleaseWebFiles (Join-Path $repo 'frontend\dist') (Join-Path $work 'wwwroot')
$schema=(Get-ChildItem (Join-Path $repo 'backend\src\AlShifa.Api\Infrastructure\Migrations') -Filter '2*.cs' | Where-Object {$_.Name -notlike '*.Designer.cs'} | Sort-Object Name | Select-Object -Last 1).BaseName
[IO.File]::WriteAllText((Join-Path $work 'release.json'), (@{version=$Version;schema=$schema}|ConvertTo-Json))
# Settings of the development computer never travel with a release.
Remove-Item (Join-Path $work 'appsettings.Development.json') -ErrorAction SilentlyContinue
if (Test-Path -LiteralPath (Join-Path $work 'appsettings.Production.json')) { throw 'Production configuration must not be included in a release.' }

$archive = "$work.tar.gz"
Invoke-Native 'tar.exe' @('-czf', $archive, '-C', $work, '.') | Out-Null
Remove-Item $work -Recurse -Force
$hash = (Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant()
"Archive: $archive"
"SHA-256: $hash"
"On the server: deploy/linux/deploy-release.sh /tmp/$(Split-Path $archive -Leaf) $hash"
