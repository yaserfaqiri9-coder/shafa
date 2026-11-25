# Creates one immutable office-signed job. Transport to the relay is a separate explicit tool step.
param(
    [Parameter(Mandatory)][string]$SessionId,
    [Parameter(Mandatory)][string]$AgentId,
    [Parameter(Mandatory)][long]$Sequence,
    [Parameter(Mandatory)][ValidateSet('reconcile_readonly','repair_prerequisite','collect','prepare_connection','activate_sync','verify_sync','finish')][string]$Kind,
    [string]$ScriptFile,
    [string]$ApprovedEvidenceSha256,
    [switch]$CompatibilityAndDuplicateReviewPassed,
    [switch]$ActualSyncAccepted,
    [string]$AcceptanceProof
)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
if($SessionId -notmatch '^connect-[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}$' -or $AgentId -notmatch '^[a-f0-9]{64}$' -or $Sequence -le 0){throw 'Invalid bound job identity'}
if($Kind -in @('prepare_connection','activate_sync') -and (-not $CompatibilityAndDuplicateReviewPassed -or $ApprovedEvidenceSha256 -notmatch '^[a-f0-9]{64}$')){throw 'Reviewed evidence is mandatory before connection preparation or activation'}
if($ActualSyncAccepted -and $Kind -ne 'finish'){throw 'Only a final job can mark practical acceptance'}
if($ActualSyncAccepted){
    if(-not $AcceptanceProof){throw 'Real acceptance proof is mandatory'}
    $proof=Get-Content -LiteralPath $AcceptanceProof -Raw -Encoding UTF8|ConvertFrom-Json
    foreach($flag in 'realHospital','bidirectionalPassed','realInternetDisconnectRecovery','queueReconciled','duplicatesAbsent'){if($proof.$flag -ne $true){throw 'Real acceptance proof is incomplete'}}
    if($proof.sessionId -ne $SessionId){throw 'Acceptance proof session mismatch'}
}
$private=Join-Path $repo ('.work\sync-review-private\'+$SessionId)
$manifest=Get-Content -LiteralPath (Join-Path $private 'package.json') -Raw -Encoding UTF8|ConvertFrom-Json
Add-Type -Path (Join-Path $repo 'deploy/windows/sync-review/ReviewCrypto.cs') -ReferencedAssemblies System.Core,System.Security,System.Web.Extensions
$code=if($ScriptFile){[IO.File]::ReadAllText([IO.Path]::GetFullPath($ScriptFile))}else{''}
if(-not $code -and $Kind -notin @('finish','collect')){throw 'Execution job needs reviewed source'}
$payload=[ordered]@{sessionId=$SessionId;installationId=$manifest.installationId;agentId=$AgentId;sequence=$Sequence;kind=$Kind;expiresAt=[DateTimeOffset]::UtcNow.AddMinutes(30).ToString('o');script=$code;scriptSha256=[AlShifa.SyncReview.Crypto]::TextHash($code);approvedEvidenceSha256=$ApprovedEvidenceSha256;compatibilityAndDuplicateReviewPassed=[bool]$CompatibilityAndDuplicateReviewPassed;actualSyncAccepted=[bool]$ActualSyncAccepted}
$bytes=[Text.Encoding]::UTF8.GetBytes(($payload|ConvertTo-Json -Compress -Depth 8))
$envelope=[ordered]@{body=[Convert]::ToBase64String($bytes);signature=[AlShifa.SyncReview.Crypto]::Sign((Join-Path $private 'office-private.dpapi'),$bytes)}
$job=Join-Path $private ('job-'+$Sequence+'.signed.json')
if(Test-Path -LiteralPath $job){throw 'Sequence already has a signed job; inspect existing job instead of replaying'}
[IO.File]::WriteAllText($job,($envelope|ConvertTo-Json -Compress),(New-Object Text.UTF8Encoding($false)))
@{signedJob=$job;sequence=$Sequence;kind=$Kind;sha256=(Get-FileHash -LiteralPath $job -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json -Compress
