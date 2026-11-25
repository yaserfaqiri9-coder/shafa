# No patient values, keys or installation identifiers are returned in status output.
function Get-SyncBootstrapDecision($Local,$Remote,[int]$OldPending=0) {
    $remoteIdentity=[guid]::Empty;$seq=[long]0
    $reason = if(-not $Local.KeyPresent){'sync_key_missing'}
    elseif(-not $Local.ApprovedUrl){'sync_url_requires_review'}
    elseif(-not $Local.IdentityPresent){'installation_identity_invalid'}
    elseif($Remote.protocolVersion -ne 2){'sync_protocol_mismatch'}
    elseif(-not $Remote.sourceId){'source_enrollment_required'}
    elseif(-not [guid]::TryParse([string]$Remote.sourceId,[ref]$remoteIdentity) -or $remoteIdentity -eq [guid]::Empty){'remote_identity_invalid'}
    elseif(([guid]$Remote.sourceId) -ne ([guid]$Local.Identity)){'source_conflict'}
    elseif($Remote.schema -ne $Local.Schema){'schema_mismatch'}
    elseif(-not [long]::TryParse([string]$Remote.seq,[ref]$seq) -or $seq -lt 0 -or [long]$Local.Acked -lt 0 -or [long]$Local.Sent -lt [long]$Local.Acked){'sync_state_invalid'}
    elseif([long]$Remote.seq -lt [long]$Local.Acked -or [long]$Remote.seq -gt [long]$Local.Sent){'sync_reconciliation_required'}
    elseif($OldPending -gt 0){'old_commands_require_review'}
    else{''}
    [pscustomobject]@{Ready=($reason -eq '');Reason=$reason}
}

function Invoke-BootstrapSignedRequest([string]$Url,[string]$Key,[string]$Path,[string]$Method='GET') {
    $uri=[uri]$Url
    if($uri.Scheme -ne 'https' -or $uri.DnsSafeHost -ne 'shafa.novatechsoft.com' -or $uri.Port -ne 443 -or $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/') {throw 'Unapproved sync endpoint.'}
    if($Path -notin @('/api/sync/mirror/state','/api/sync/mirror/commands/pending')){throw 'Unapproved metadata route.'}
    $stamp=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds().ToString([Globalization.CultureInfo]::InvariantCulture)
    $sha=[Security.Cryptography.SHA256]::Create()
    $mac=New-Object Security.Cryptography.HMACSHA256
    try {
        $emptyHash=([BitConverter]::ToString($sha.ComputeHash([byte[]]@()))).Replace('-','')
        $mac.Key=[Text.Encoding]::UTF8.GetBytes($Key)
        $signature=([BitConverter]::ToString($mac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$stamp`n$Method`n$Path`n$emptyHash")))).Replace('-','')
        [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
        $ProgressPreference='SilentlyContinue'
        $response=Invoke-WebRequest -UseBasicParsing -Uri ($Url.TrimEnd('/')+$Path) -Method $Method -Headers @{'X-Sync-Time'=$stamp;'X-Sync-Signature'=$signature} -MaximumRedirection 0 -TimeoutSec 15
        $response.Content | ConvertFrom-Json
    } finally {$sha.Dispose();$mac.Dispose()}
}

function Set-SyncBootstrapTask([string]$Root) {
    $taskName='AlShifa Sync Bootstrap'
    $scriptPath=Join-Path $Root 'scripts\sync-bootstrap.ps1'
    $existing=Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if($existing -and $existing.Actions[0].Arguments -notlike ('*"'+$scriptPath+'"*')){throw 'An unrelated task uses the sync bootstrap name.'}
    $statusPath=Join-Path $Root 'logs\sync-bootstrap-status.json'
    $status=if(Test-Path -LiteralPath $statusPath){Get-Content -LiteralPath $statusPath -Raw|ConvertFrom-Json}
    if($status.Ready){if($existing){Unregister-ScheduledTask -TaskName $taskName -Confirm:$false};return}
    $action=New-ScheduledTaskAction -Execute (Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe') -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "'+$scriptPath+'" -Root "'+$Root+'"')
    $startup=New-ScheduledTaskTrigger -AtStartup
    $retry=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(5) -RepetitionInterval (New-TimeSpan -Minutes 5)
    $settings=New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 3)
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($startup,$retry) -Settings $settings -User SYSTEM -RunLevel Highest -Force|Out-Null
}
