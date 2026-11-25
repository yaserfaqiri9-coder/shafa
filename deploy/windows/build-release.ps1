# Run on the development computer. Produces release\alshifa-<date>\ to copy to the hospital host.
# Self-contained: the host needs no .NET or Node installation. Contains no secrets and no patient data.
param([string]$OutputRoot)

. (Join-Path $PSScriptRoot 'common.ps1')

$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo 'release' }
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$target = Join-Path $OutputRoot "alshifa-$(Get-Date -Format 'yyyyMMdd-HHmm')"
$app = Join-Path $target 'app'

Push-Location (Join-Path $repo 'frontend')
try {
    Invoke-Native 'cmd.exe' @('/c', 'npm', 'run', 'build') | Out-Null
} finally {
    Pop-Location
}

$project = Join-Path $repo 'backend\src\AlShifa.Api\AlShifa.Api.csproj'
Invoke-Native $dotnet @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $app, '--nologo', '-v', 'q') | Out-Null

Copy-Item (Join-Path $repo 'frontend\dist') (Join-Path $app 'wwwroot') -Recurse
$version = (Get-Date).ToUniversalTime().ToString('yyyy.MM.dd.HHmm')
# The database version of this build: the online copy must accept it before the release is offered to the hospital.
$schema = (Get-ChildItem (Join-Path $repo 'backend\src\AlShifa.Api\Infrastructure\Migrations') -Filter '2*.cs' |
    Where-Object { $_.Name -notlike '*.Designer.cs' } | Sort-Object Name | Select-Object -Last 1).BaseName
[IO.File]::WriteAllText((Join-Path $app 'release.json'), (@{ version = $version; schema = $schema } | ConvertTo-Json))
Copy-Item $PSScriptRoot (Join-Path $target 'scripts') -Recurse
foreach ($guide in 'INSTALL-SIMPLE.md', 'PILOT-CHECKLIST.md', 'DEPLOYMENT-WINDOWS.md') { Copy-Item (Join-Path $repo $guide) $target }
"Release ready: $target"
