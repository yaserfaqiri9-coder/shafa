# Outbound-only, expiring operator session. Only office-signed jobs can execute.
function Invoke-SessionHttp($Relay,[string]$AgentId,[string]$Path,[string]$Method='GET',$Body=$null) {
    $base=[uri]$Relay.url
    if($base.Scheme -ne 'https' -or $base.DnsSafeHost -ne 'shafa.novatechsoft.com' -or $base.Port -ne 443 -or $base.UserInfo -or $base.Query -or $base.Fragment){throw 'session_endpoint_invalid'}
    if($Path -notin @('claim','evidence','job','result','heartbeat')){throw 'session_route_invalid'}
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    $request=[Net.HttpWebRequest]::Create($Relay.url.TrimEnd('/')+'/'+$Path)
    $request.Method=$Method;$request.AllowAutoRedirect=$false;$request.Timeout=30000;$request.ReadWriteTimeout=30000
    $request.Headers['Authorization']='Bearer '+$Relay.token
    if($AgentId){$request.Headers['X-AlShifa-Agent']=$AgentId}
    if($null -ne $Body){
        $bytes=[Text.Encoding]::UTF8.GetBytes(($Body|ConvertTo-Json -Depth 12 -Compress))
        $request.ContentType='application/json';$request.ContentLength=$bytes.Length
        $stream=$request.GetRequestStream();try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
    }
    $response=$request.GetResponse()
    try{$reader=New-Object IO.StreamReader($response.GetResponseStream(),[Text.Encoding]::UTF8);try{return ($reader.ReadToEnd()|ConvertFrom-Json)}finally{$reader.Dispose()}}finally{$response.Close()}
}
function Write-SessionState([string]$Path,$State) {
    $tmp=$Path+'.'+[guid]::NewGuid().ToString('N')+'.partial'
    [IO.File]::WriteAllText($tmp,($State|ConvertTo-Json -Depth 12),(New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $Path -Force
}
function Assert-SessionJob($Envelope,$Manifest,[string]$AgentId,[string]$PublicKey,[long]$LastSequence) {
    $body=[Convert]::FromBase64String([string]$Envelope.body)
    if(-not [AlShifa.SyncReview.Crypto]::Verify($PublicKey,$body,[string]$Envelope.signature)){throw 'job_signature_invalid'}
    $job=[Text.Encoding]::UTF8.GetString($body)|ConvertFrom-Json
    if($job.sessionId -ne $Manifest.packageId -or $job.installationId -ne $Manifest.installationId -or $job.agentId -ne $AgentId){throw 'job_identity_mismatch'}
    if([DateTimeOffset]::Parse($job.expiresAt) -lt [DateTimeOffset]::UtcNow){throw 'job_expired'}
    if([long]$job.sequence -le $LastSequence){throw 'job_already_seen'}
    if($job.kind -notin @('reconcile_readonly','repair_prerequisite','collect','prepare_connection','activate_sync','verify_sync','finish')){throw 'job_kind_invalid'}
    if([AlShifa.SyncReview.Crypto]::TextHash([string]$job.script) -ne $job.scriptSha256){throw 'job_script_hash_invalid'}
    return $job
}
function Invoke-HospitalSessionCore([string]$PackageDirectory) {
    $manifest=Get-Content -LiteralPath (Join-Path $PackageDirectory 'package.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $relay=Get-Content -LiteralPath (Join-Path $PackageDirectory 'relay.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $public=[IO.File]::ReadAllText((Join-Path $PackageDirectory 'office-public.xml'))
    if($relay.sessionId -ne $manifest.packageId -or [DateTimeOffset]::Parse($relay.expiresAt) -le [DateTimeOffset]::UtcNow){throw 'session_expired_or_wrong'}
    $service=Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
    if(-not $service -or $service.PathName -notmatch '^"?([A-Za-z]:\\[^"\r\n]+\\app\\AlShifa\.Api\.exe)"?$'){throw 'session_installation_not_found'}
    $root=Split-Path (Split-Path $Matches[1] -Parent) -Parent
    $identityPath=Join-Path $root 'keys\sync-installation-id';[void](Assert-ReviewPath $identityPath $root)
    if([IO.File]::ReadAllText($identityPath).Trim() -ne $manifest.installationId){throw 'session_wrong_installation'}
    $parent=Join-Path $root 'sync-operator-sessions';[void](Assert-ReviewPath $parent $root);Protect-ReviewDirectory $parent
    $directory=Join-Path $parent $manifest.packageId;[void](Assert-ReviewPath $directory $parent);Protect-ReviewDirectory $directory
    $statePath=Join-Path $directory 'state-private.json'
    if(Test-Path -LiteralPath $statePath){$state=Get-Content -LiteralPath $statePath -Raw -Encoding UTF8|ConvertFrom-Json}
    else{$state=[pscustomobject]@{agentId=[AlShifa.SyncReview.Crypto]::Hash([AlShifa.SyncReview.Crypto]::Random(32));lastSequence=0L;jobState='idle';initialCollected=$false;initialSucceeded=$false;initialEvidenceHash=$null;initialEnvelopePath=$null;pendingResultPath=$null;finished=$false;syncAccepted=$false};Write-SessionState $statePath $state}
    if($state.finished -and -not $state.pendingResultPath){if($state.syncAccepted){Write-Output 'RESULT:SYNC_ACCEPTED';return 0};Write-Output 'RESULT:SESSION_HELD';return 2}
    $claimed=$false
    while([DateTimeOffset]::UtcNow -lt [DateTimeOffset]::Parse($relay.expiresAt)){
        if(Test-Path -LiteralPath (Join-Path $PackageDirectory 'session-stop.request')){Write-Output 'RESULT:SESSION_STOPPED';return 2}
        try{
            if(-not $claimed){
                [void](Invoke-SessionHttp $relay $state.agentId 'claim' 'POST' @{sessionId=$manifest.packageId;installationId=$manifest.installationId;agentId=$state.agentId})
                $claimed=$true;Write-Output 'STAGE:CONNECTED'
            }
            if(-not $state.initialCollected){
                $return=Join-Path $directory ('initial-'+[guid]::NewGuid().ToString('N'))
                $lines=@(Invoke-HospitalSyncReview $PackageDirectory $return)
                foreach($line in $lines|Where-Object {$_ -is [string] -and $_ -match '^STAGE:'}){Write-Output $line}
                $file=Get-ChildItem -LiteralPath $return -Filter '*.alshifa-review'|Select-Object -First 1
                if(-not $file){throw 'initial_evidence_missing'}
                $state.initialEnvelopePath=$file.FullName;$state.initialCollected=$true;$state.initialSucceeded=([int]$lines[-1] -eq 0)
                $state.initialEvidenceHash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                Write-SessionState $statePath $state
            }
            # Re-uploading exactly the same ciphertext is idempotent in the relay.
            [void](Invoke-SessionHttp $relay $state.agentId 'evidence' 'POST' @{envelope=[IO.File]::ReadAllText($state.initialEnvelopePath)})
            if($state.pendingResultPath){
                $out=Get-Content -LiteralPath $state.pendingResultPath -Raw -Encoding UTF8|ConvertFrom-Json
                [void](Invoke-SessionHttp $relay $state.agentId 'result' 'POST' $out)
                $state.pendingResultPath=$null;Write-SessionState $statePath $state
            }
            if($state.finished){if($state.syncAccepted){Write-Output 'RESULT:SYNC_ACCEPTED';return 0};Write-Output 'RESULT:SESSION_HELD';return 2}
            [void](Invoke-SessionHttp $relay $state.agentId 'heartbeat' 'POST' @{alive=$true})
            $response=Invoke-SessionHttp $relay $state.agentId 'job'
            if($response.job){
                $job=$null
                try{$job=Assert-SessionJob $response.job $manifest $state.agentId $public ([long]$state.lastSequence)}catch{}
                if($job){
                    if($job.kind -in @('prepare_connection','activate_sync')){
                        if(-not $state.initialSucceeded -or $job.approvedEvidenceSha256 -ne $state.initialEvidenceHash -or $job.compatibilityAndDuplicateReviewPassed -ne $true){throw 'activation_gate_not_passed'}
                    }
                    $state.lastSequence=[long]$job.sequence;$state.jobState='in_progress';Write-SessionState $statePath $state
                    Write-Output ('STAGE:JOB_'+$job.kind.ToUpperInvariant())
                    $result=[ordered]@{sessionId=$manifest.packageId;sequence=$job.sequence;kind=$job.kind;completedAt=$null;exitCode=$null;structured=$null;output=$null;actualSyncAccepted=$false}
                    if($job.kind -eq 'finish'){
                        $state.finished=$true;$state.syncAccepted=($job.actualSyncAccepted -eq $true)
                        $result.exitCode=0;$result.actualSyncAccepted=$state.syncAccepted
                    }elseif($job.kind -eq 'collect'){
                        $return=Join-Path $directory ('collect-'+[guid]::NewGuid().ToString('N'))
                        $lines=@(Invoke-HospitalSyncReview $PackageDirectory $return)
                        $file=Get-ChildItem -LiteralPath $return -Filter '*.alshifa-review'|Select-Object -First 1
                        if(-not $file){throw 'fresh_evidence_missing'}
                        $state.initialEnvelopePath=$file.FullName;$state.initialEvidenceHash=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant();$state.initialSucceeded=([int]$lines[-1] -eq 0)
                        $result.exitCode=[int]$lines[-1];$result.structured=@{freshEvidenceAvailable=$true}
                    }else{
                        $codePath=Join-Path $directory ('job-'+$job.sequence+'.ps1');$resultPath=Join-Path $directory ('job-'+$job.sequence+'-structured-private.json')
                        if(Test-Path -LiteralPath $codePath){throw 'job_file_already_exists'}
                        function PsLiteral([string]$Text){"'"+$Text.Replace("'","''")+"'"}
                        $prefix='$env:ALSHIFA_SYNC_PACKAGE_DIR='+(PsLiteral $PackageDirectory)+"`r`n"+'$env:ALSHIFA_SYNC_ROOT='+(PsLiteral $root)+"`r`n"+'$env:ALSHIFA_SYNC_SESSION_DIR='+(PsLiteral $directory)+"`r`n"+'$env:ALSHIFA_SYNC_RESULT='+(PsLiteral $resultPath)+"`r`n"
                        [IO.File]::WriteAllText($codePath,($prefix+[string]$job.script),(New-Object Text.UTF8Encoding($true)))
                        $errorFile=Join-Path $directory ('job-'+$job.sequence+'-stderr-private.txt')
                        $native=[AlShifa.SyncReview.Native]::Run((Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'),@('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$codePath),$null,$errorFile,1800000)
                        $result.exitCode=$native.ExitCode;$result.output=$native.Output
                        if(Test-Path -LiteralPath $resultPath){$result.structured=Get-Content -LiteralPath $resultPath -Raw -Encoding UTF8|ConvertFrom-Json}
                    }
                    $result.completedAt=[DateTimeOffset]::UtcNow.ToString('o')
                    $sealed=[AlShifa.SyncReview.Crypto]::Seal($public,$manifest.packageId,[Text.Encoding]::UTF8.GetBytes(($result|ConvertTo-Json -Depth 30 -Compress)))
                    $resultFile=Join-Path $directory ('job-'+$job.sequence+'-result-encrypted.json')
                    [IO.File]::WriteAllText($resultFile,(@{envelope=$sealed}|ConvertTo-Json -Depth 4 -Compress),(New-Object Text.UTF8Encoding($false)))
                    $state.pendingResultPath=$resultFile;$state.jobState='completed';Write-SessionState $statePath $state
                    [void](Invoke-SessionHttp $relay $state.agentId 'result' 'POST' @{envelope=$sealed})
                    $state.pendingResultPath=$null;Write-SessionState $statePath $state
                    if($state.finished){if($state.syncAccepted){Write-Output 'RESULT:SYNC_ACCEPTED';return 0};Write-Output 'RESULT:SESSION_HELD';return 2}
                }
            }
            Write-Output 'STAGE:WAITING'
            Start-Sleep -Seconds 10
        }catch{
            # No raw exception, native output, credentials, or report enters the UI.
            Write-Output 'STAGE:RETRY'
            $claimed=$false
            Start-Sleep -Seconds 15
        }
    }
    Write-Output 'RESULT:SESSION_EXPIRED';return 2
}
function Invoke-HospitalSession([string]$PackageDirectory) {
    $mutex=New-Object Threading.Mutex($false,'Global\AlShifa.SyncOperatorSession')
    $owns=$false
    try{
        try{$owns=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owns=$true}
        if(-not $owns){Write-Output 'RESULT:SESSION_ALREADY_RUNNING';return 3}
        Invoke-HospitalSessionCore $PackageDirectory
    }finally{if($owns){$mutex.ReleaseMutex()};$mutex.Dispose()}
}
