# Decrypts only returned evidence, into a private office folder. Never activates Sync.
param([Parameter(Mandatory)][string]$Evidence)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if((Get-Item -LiteralPath $Evidence).Length -gt 128MB){throw 'Evidence file too large'}
$envelope=[IO.File]::ReadAllText([IO.Path]::GetFullPath($Evidence))
$header=$envelope|ConvertFrom-Json
if($header.packageId -notmatch '^(review|connect)-[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}$' -or $header.format -ne 'AlShifa.SyncReview.1'){throw 'Unrecognized review package'}
$private=Join-Path $repo ('.work\sync-review-private\'+$header.packageId)
$manifest=Get-Content -LiteralPath (Join-Path $private 'package.json') -Raw -Encoding UTF8|ConvertFrom-Json
Add-Type -Path (Join-Path $repo 'deploy\windows\sync-review\ReviewCrypto.cs') -ReferencedAssemblies System.Core,System.Security,System.Web.Extensions
$bytes=[AlShifa.SyncReview.Crypto]::Open((Join-Path $private 'office-private.dpapi'),$manifest.packageId,$envelope)
$payload=[Text.Encoding]::UTF8.GetString($bytes);$evidenceData=$payload|ConvertFrom-Json
if($evidenceData.format -ne 'AlShifa.HospitalEvidence.1' -or $evidenceData.packageId -ne $manifest.packageId -or ($evidenceData.installationId -and $evidenceData.installationId -ne $manifest.installationId)){throw 'Evidence identity mismatch'}
foreach($flag in 'replicationInvoked','productionDatabaseWrites','keyIssued','sourceIdChanged','configurationChanged','commandsReplayed','activationAuthorized'){if($evidenceData.$flag -ne $false){throw 'Unexpected evidence side effect claim'}}
$output=Join-Path $private ('hospital-evidence-'+[guid]::NewGuid().ToString('N')+'.json')
[IO.File]::WriteAllText($output,$payload,(New-Object Text.UTF8Encoding($false)))
@{decrypted=$true;success=$evidenceData.success;privateEvidencePath=$output;queueCount=$evidenceData.queueCount;backupRestoreVerified=$evidenceData.backupRestoreVerified;onlineReconciliationRequired=$true;activationPerformed=$false}|ConvertTo-Json -Compress
