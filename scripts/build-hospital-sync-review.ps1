# Builds an offline, hospital-bound assessment executable. Never embeds Sync keys or records.
param([Parameter(Mandatory)][string]$RelayConfig,[string]$InstallationId='0ab50744-3dc0-40d4-9662-50c1332ca4c2')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$relay=Get-Content -LiteralPath $RelayConfig -Raw -Encoding UTF8|ConvertFrom-Json
$id=[string]$relay.sessionId
if($id -notmatch '^connect-[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}$' -or $relay.installationId -ne $InstallationId){throw 'Relay identity mismatch'}
$private=Join-Path $repo ('.work\sync-review-private\'+$id)
$stage=Join-Path $repo ('.tools\sync-review-build\'+$id)
$output=Join-Path $repo ('outputs\AlShifa-Sync-Connect-'+$id)
foreach($path in $private,$stage,$output){if(Test-Path -LiteralPath $path){throw 'Existing package path'};[void][IO.Directory]::CreateDirectory($path)}
Add-Type -Path (Join-Path $repo 'deploy\windows\sync-review\ReviewCrypto.cs') -ReferencedAssemblies System.Core,System.Security,System.Web.Extensions
. (Join-Path $repo 'deploy\windows\sync-review\Review.Core.ps1')
Protect-ReviewDirectory $private
$public=[AlShifa.SyncReview.Crypto]::NewProtectedKey((Join-Path $private 'office-private.dpapi'))
[IO.File]::WriteAllText((Join-Path $stage 'office-public.xml'),$public)
foreach($file in 'Review.ps1','Review.Core.ps1','ReviewCrypto.cs','Session.Agent.ps1'){Copy-Item -LiteralPath (Join-Path $repo ('deploy\windows\sync-review\'+$file)) -Destination $stage}
$relayPayload=[ordered]@{sessionId=$relay.sessionId;url=$relay.url;token=$relay.token;expiresAt=$relay.expiresAt}
[IO.File]::WriteAllText((Join-Path $stage 'relay.json'),($relayPayload|ConvertTo-Json -Depth 5),(New-Object Text.UTF8Encoding($false)))
$request=Get-Content -LiteralPath (Join-Path $repo 'outputs\hospital-sync-review-20261123\connection-review.pending.json') -Raw -Encoding UTF8|ConvertFrom-Json
$snapshot=[IO.File]::ReadAllText((Join-Path $repo 'backend\src\AlShifa.Api\Infrastructure\Migrations\AppDbContextModelSnapshot.cs'))
$modelTables=@([regex]::Matches($snapshot,'\.ToTable\("([A-Za-z0-9_]+)"')|ForEach-Object {$_.Groups[1].Value}|Sort-Object -Unique)
if($modelTables.Count -lt 50 -or $modelTables -notcontains 'Patients' -or $modelTables -notcontains 'SyncCommands'){throw 'Current mapped table manifest incomplete'}
$policy=[ordered]@{modelTables=$modelTables;modelSnapshotSha256=[AlShifa.SyncReview.Crypto]::TextHash($snapshot);localOnlyTables=@('SyncCommands','SyncChanges','SyncStates','OutboxEvents','IdempotencyRecords','NumberSequences','AspNetUserTokens','AspNetUserLogins');localOnlyColumns=@{AspNetUsers=@('PasswordHash','SecurityStamp','ConcurrencyStamp','LockoutEnd','LockoutEnabled','AccessFailedCount','TwoFactorEnabled')}}
$manifest=[ordered]@{format='AlShifa.SyncReviewPackage.1';packageId=$id;installationId=$InstallationId;schema='20261122151006_OperationsAndStaffPayroll';createdAt=[DateTimeOffset]::UtcNow.ToString('o');officePublicKeyId=[AlShifa.SyncReview.Crypto]::TextHash($public);receiptTargets=@($request.receiptTargets);syncPolicy=$policy;replicationAuthorized=$false;resetAuthorized=$false;keyIssuanceAuthorized=$false}
[IO.File]::WriteAllText((Join-Path $stage 'package.json'),($manifest|ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $private 'package.json'),($manifest|ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
$zip=Join-Path $private 'payload.zip';[string[]]$paths=@(Get-ChildItem -LiteralPath $stage -File|ForEach-Object {$_.FullName});Compress-Archive -LiteralPath $paths -DestinationPath $zip
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
$source=[IO.File]::ReadAllText((Join-Path $repo 'deploy\windows\sync-review\ReviewLauncher.cs')).Replace('__PAYLOAD_SHA256__',$hash)
[IO.File]::WriteAllText((Join-Path $stage 'ReviewLauncher.cs'),$source,(New-Object Text.UTF8Encoding($false)))
$uac='<?xml version="1.0" encoding="utf-8"?><assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0"><trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges><requestedExecutionLevel level="requireAdministrator" uiAccess="false" /></requestedPrivileges></security></trustInfo></assembly>'
[IO.File]::WriteAllText((Join-Path $stage 'uac.manifest'),$uac)
$csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe=Join-Path $output 'AlShifa-Sync-Connect.exe'
& $csc /nologo /target:winexe /platform:anycpu /optimize+ ("/out:$exe") ("/win32manifest:"+(Join-Path $stage 'uac.manifest')) ("/resource:$zip,payload.zip") /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.IO.Compression.dll (Join-Path $stage 'ReviewLauncher.cs')
if($LASTEXITCODE -ne 0){throw 'Review executable compilation failed'}
$record=[ordered]@{packageId=$id;executable=$exe;sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant();privatePath=$private;payloadSha256=$hash;buildStage=$stage;containsSyncKey=$false;containsPatientData=$false}
[IO.File]::WriteAllText((Join-Path $private 'build.json'),($record|ConvertTo-Json -Depth 6),(New-Object Text.UTF8Encoding($false)))
[IO.File]::WriteAllText((Join-Path $output 'SHA256.txt'),($record.sha256+'  AlShifa-Sync-Connect.exe'))
$record|ConvertTo-Json
