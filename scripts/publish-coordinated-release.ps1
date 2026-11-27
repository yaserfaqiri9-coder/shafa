# Deploys an already verified, frozen Linux/Windows release pair. No build from a dirty worktree.
# The server proves a private backup restore and service-user startup before code-only activation.
param(
 [Parameter(Mandatory)][string]$WindowsRelease,
 [Parameter(Mandatory)][string]$LinuxArchive,
 [Parameter(Mandatory)][string]$Server,
 [Parameter(Mandatory)][string]$SshKey,
 [Parameter(Mandatory)][string]$ExpectedDatabase,
 [string]$PrivateKeyPath=(Join-Path $env:USERPROFILE '.alshifa-publisher\update-private.xml'),
 [switch]$PrepareOnly,
 [switch]$PerformanceIndexUpgrade
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')
$marker=Get-Content -LiteralPath (Join-Path $WindowsRelease 'app\release.json') -Raw|ConvertFrom-Json
if($marker.version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $marker.schema -notmatch '^20\d{12}_[A-Za-z0-9_]+$' -or $ExpectedDatabase -notmatch '^alshifa_(mirror|rb_[0-9_a-f]+)$'){throw 'Invalid coordinated release metadata.'}
if($PerformanceIndexUpgrade -and $marker.schema -ne '20261127080000_PerformanceIndexes'){throw 'Only the verified ledger index migration is supported by this deployment mode.'}
$archive=(Resolve-Path -LiteralPath $LinuxArchive).Path
$hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
$id=(Get-Date).ToUniversalTime().ToString('yyyyMMdd')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)
$remote='/root/alshifa-coordinated-'+$id
$ssh=@('-i',(Resolve-Path -LiteralPath $SshKey).Path,'-o','BatchMode=yes','-o','ConnectTimeout=15')
$evidence=Join-Path $repo ('.tools\coordinated-release-'+$id)
New-Item -ItemType Directory $evidence|Out-Null
& (Join-Path $repo 'deploy\windows\publish-update.ps1') -Release $WindowsRelease -Server $Server -SshKey $SshKey -PrivateKeyPath $PrivateKeyPath -CheckOnly
$signed=Join-Path $repo ('.tools\update-publish\'+$marker.version)
foreach($file in 'manifest.json','manifest.json.sig',("alshifa-"+$marker.version+'.zip')){if(-not(Test-Path -LiteralPath (Join-Path $signed $file))){throw 'Signed Windows release was not prepared.'}}
Invoke-Native 'ssh.exe' ($ssh+@($Server,"install -d -m 700 $remote"))|Out-Null
Invoke-Native 'scp.exe' ($ssh+@($archive,(Join-Path $repo 'deploy\linux\deploy-verified-release.py'),(Join-Path $repo 'deploy\linux\publish-hospital-update.sh'),(Join-Path $signed 'manifest.json'),(Join-Path $signed 'manifest.json.sig'),(Join-Path $signed ("alshifa-"+$marker.version+'.zip')),"${Server}:$remote/"))|Out-Null
$arguments="--id $id --archive $remote/$(Split-Path $archive -Leaf) --sha256 $hash --version $($marker.version) --schema $($marker.schema) --expected-database $ExpectedDatabase"
if($PerformanceIndexUpgrade){$arguments+=' --performance-index-upgrade'}
$prepare=Invoke-Native 'ssh.exe' ($ssh+@($Server,"python3 $remote/deploy-verified-release.py $arguments"))
$prepared=($prepare|Select-Object -Last 1)|ConvertFrom-Json
$prepared|ConvertTo-Json -Depth 8|Set-Content (Join-Path $evidence 'preparation.json') -Encoding UTF8
if(-not $prepared.passed){throw 'Online preparation did not pass.'}
if($PrepareOnly){"Prepared only; no online switch or hospital publication. Evidence: $evidence";return}
$activate=Invoke-Native 'ssh.exe' ($ssh+@($Server,"python3 $remote/deploy-verified-release.py $arguments --activate"))
$activated=($activate|Select-Object -Last 1)|ConvertFrom-Json
$activated|ConvertTo-Json -Depth 8|Set-Content (Join-Path $evidence 'activation.json') -Encoding UTF8
if(-not $activated.passed -or ($activated.productionDatabaseWrites -and -not $PerformanceIndexUpgrade) -or -not $activated.publicAccessRestored){throw 'Online activation was not confirmed.'}
if($PerformanceIndexUpgrade -and (-not $activated.indexMigrationApplied -or -not $activated.existingRowsPreserved -or -not $activated.temporaryMigrationAccountRemoved -or -not $activated.previousSourceSchemasRetained)){throw 'Index migration preservation was not confirmed; hospital update was not published.'}
Invoke-Native 'ssh.exe' ($ssh+@($Server,"bash $remote/publish-hospital-update.sh $remote"))
$publisher=Get-Content -LiteralPath (Join-Path $repo 'deploy\windows\update-publisher.json') -Raw|ConvertFrom-Json
. (Join-Path $repo 'deploy\windows\auto-update-common.ps1')
Receive-UpdateFile ([uri]$publisher.manifestUrl) (Join-Path $evidence 'manifest.json') 65536
Receive-UpdateFile ([uri]($publisher.manifestUrl+'.sig')) (Join-Path $evidence 'manifest.sig') 8192
$publicKey=Join-Path $evidence 'publisher-public.xml';[IO.File]::WriteAllText($publicKey,[string]$publisher.publicKey)
$verified=Assert-UpdateManifest ([IO.File]::ReadAllBytes((Join-Path $evidence 'manifest.json'))) ([Convert]::FromBase64String([IO.File]::ReadAllText((Join-Path $evidence 'manifest.sig')).Trim())) $publicKey
if($verified.version -ne $marker.version -or (Get-FileHash -LiteralPath (Join-Path $evidence 'manifest.json')).Hash -cne (Get-FileHash -LiteralPath (Join-Path $signed 'manifest.json')).Hash){throw 'Public hospital update does not match the signed release.'}
@{version=$marker.version;schema=$marker.schema;onlineActivated=$true;productionDatabaseWrites=[bool]$activated.productionDatabaseWrites;existingBusinessRowsPreserved=[bool]$activated.existingRowsPreserved;hospitalUpdatePublished=$true;evidence=$evidence}|ConvertTo-Json
