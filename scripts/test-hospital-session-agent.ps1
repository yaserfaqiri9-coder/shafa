$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$work=Join-Path $repo ('.tools\session-agent-lab-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
Add-Type -Path (Join-Path $repo 'deploy/windows/sync-review/ReviewCrypto.cs') -ReferencedAssemblies System.Core,System.Security,System.Web.Extensions
. (Join-Path $repo 'deploy/windows/sync-review/Review.Core.ps1')
. (Join-Path $repo 'deploy/windows/sync-review/Session.Agent.ps1')
Protect-ReviewDirectory $work
$private=Join-Path $work 'office-private.dpapi';$public=[AlShifa.SyncReview.Crypto]::NewProtectedKey($private)
[IO.File]::WriteAllText((Join-Path $work 'office-public.xml'),$public)
$identity=[guid]::NewGuid().ToString('D');$manifest=[pscustomobject]@{packageId='synthetic-session';installationId=$identity}
[IO.File]::WriteAllText((Join-Path $work 'package.json'),($manifest|ConvertTo-Json))
$relay=@{sessionId='synthetic-session';url='https://shafa.novatechsoft.com/synthetic/';token='synthetic-nonfunctional-token';expiresAt=[DateTimeOffset]::UtcNow.AddMinutes(5).ToString('o')}
[IO.File]::WriteAllText((Join-Path $work 'relay.json'),($relay|ConvertTo-Json))
[void][IO.Directory]::CreateDirectory((Join-Path $work 'hospital\app'));[void][IO.Directory]::CreateDirectory((Join-Path $work 'hospital\keys'))
[IO.File]::WriteAllText((Join-Path $work 'hospital\keys\sync-installation-id'),$identity)
$checks=New-Object 'Collections.Generic.List[string]'
function Check([bool]$Ok,[string]$Name){if(-not $Ok){throw ('Agent synthetic check failed: '+$Name)};$checks.Add($Name)}
function Job([string]$Agent,[long]$Sequence,[string]$Kind,[string]$Script,[string]$Hash=$null){
    $body=@{sessionId=$manifest.packageId;installationId=$identity;agentId=$Agent;sequence=$Sequence;expiresAt=[DateTimeOffset]::UtcNow.AddMinutes(2).ToString('o');kind=$Kind;script=$Script;scriptSha256=[AlShifa.SyncReview.Crypto]::TextHash($Script);actualSyncAccepted=$false;approvedEvidenceSha256=$Hash;compatibilityAndDuplicateReviewPassed=$true}
    $bytes=[Text.Encoding]::UTF8.GetBytes(($body|ConvertTo-Json -Compress))
    return [pscustomobject]@{body=[Convert]::ToBase64String($bytes);signature=[AlShifa.SyncReview.Crypto]::Sign($private,$bytes)}
}
$valid=Job ('a'*64) 1 'reconcile_readonly' 'synthetic'
Check ((Assert-SessionJob $valid $manifest ('a'*64) $public 0).sequence -eq 1) 'office signature verified'
$bad=[pscustomobject]@{body=$valid.body;signature=[Convert]::ToBase64String([byte[]](1..32))};$reject=$false
try{[void](Assert-SessionJob $bad $manifest ('a'*64) $public 0)}catch{$reject=$true};Check $reject 'unsigned or tampered job rejected'
$reject=$false;try{[void](Assert-SessionJob $valid $manifest ('b'*64) $public 0)}catch{$reject=$true};Check $reject 'wrong recipient agent rejected'
$reject=$false;try{[void](Assert-SessionJob $valid $manifest ('a'*64) $public 1)}catch{$reject=$true};Check $reject 'old sequence rejected'
$script:AgentWork=$work;$script:PublicKey=$public;$script:UploadFailures=0;$script:JobResults=0;$script:ClaimedAgent=$null;$script:InitialCalls=0;$script:LastPostedEnvelope=$null
function Get-CimInstance($ClassName,$Filter){return [pscustomobject]@{PathName=('"'+(Join-Path $script:AgentWork 'hospital\app\AlShifa.Api.exe')+'"')}}
function Start-Sleep($Seconds){}
function Invoke-HospitalSyncReview($PackageDirectory,$ReturnDirectory){
    $script:InitialCalls++;[void][IO.Directory]::CreateDirectory($ReturnDirectory)
    $sealed=[AlShifa.SyncReview.Crypto]::Seal($script:PublicKey,'synthetic-session',[Text.Encoding]::UTF8.GetBytes('{"synthetic":true}'))
    [IO.File]::WriteAllText((Join-Path $ReturnDirectory 'synthetic.alshifa-review'),$sealed)
    return 0
}
function Invoke-SessionHttp($Relay,$AgentId,$Path,$Method='GET',$Body=$null){
    switch($Path){
        'claim' {$script:ClaimedAgent=$AgentId;return @{claimed=$true}}
        'evidence' {return @{received=$true}}
        'heartbeat' {return @{received=$true}}
        'result' {if($script:UploadFailures -eq 0){$script:UploadFailures++;throw 'synthetic_lost_ack'};$script:JobResults++;$script:LastPostedEnvelope=$Body.envelope;return @{received=$true}}
        'job' {
            $state=Get-Content -LiteralPath (Join-Path $script:AgentWork 'hospital\sync-operator-sessions\synthetic-session\state-private.json') -Raw|ConvertFrom-Json
            if($state.lastSequence -eq 0){
                $code='[IO.File]::AppendAllText((Join-Path $env:ALSHIFA_SYNC_SESSION_DIR "synthetic-executed.txt"),"x"); [IO.File]::WriteAllText($env:ALSHIFA_SYNC_RESULT,''{"synthetic":true,"verified":true}'')'
                return @{job=(Job $AgentId 1 'reconcile_readonly' $code)}
            }
            return @{job=(Job $AgentId 2 'finish' '')}
        }
    }
    throw 'Unexpected synthetic route'
}
$lines=@(Invoke-HospitalSession $work)
Check ($lines[-1] -eq 2 -and $lines -contains 'RESULT:SESSION_HELD') 'no false Sync completion on unsigned acceptance state'
$session=Join-Path $work 'hospital\sync-operator-sessions\synthetic-session'
Check ([IO.File]::ReadAllText((Join-Path $session 'synthetic-executed.txt')) -ceq 'x') 'signed job executes once despite lost result acknowledgement'
Check ($script:InitialCalls -eq 1 -and $script:UploadFailures -eq 1 -and $script:JobResults -eq 2) 'encrypted evidence and results reconnect automatically'
$state=Get-Content -LiteralPath (Join-Path $session 'state-private.json') -Raw|ConvertFrom-Json
Check ($state.finished -and -not $state.syncAccepted -and $state.lastSequence -eq 2 -and -not $state.pendingResultPath) 'durable sequence and completion state preserved'
$last=[Text.Encoding]::UTF8.GetString([AlShifa.SyncReview.Crypto]::Open($private,'synthetic-session',$script:LastPostedEnvelope))|ConvertFrom-Json
Check ($last.kind -eq 'finish' -and -not $last.actualSyncAccepted) 'result remains encrypted to office key'
[IO.File]::WriteAllText((Join-Path $work 'result.json'),(@{passed=$true;synthetic=$true;checks=@($checks);productionChanged=$false}|ConvertTo-Json -Depth 8))
@{passed=$true;checkCount=$checks.Count;evidencePath=$work}|ConvertTo-Json -Compress
