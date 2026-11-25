# Synthetic decision/installer-adapter tests. No real service, task, database or network access.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\sync-bootstrap-common.ps1')
$work=Join-Path $repo ('.tools\sync-bootstrap-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work|Out-Null
$source='11111111-1111-4111-8111-111111111111'
$schema='20261122151006_OperationsAndStaffPayroll'
$local=@{KeyPresent=$true;ApprovedUrl=$true;IdentityPresent=$true;Identity=$source;Schema=$schema;Acked=10L;Sent=12L}
$remote=@{protocolVersion=2;sourceId=$source;schema=$schema;seq=11L}
$cases=@(
    @{Name='compatible';Reason='';Local=@{};Remote=@{}},
    @{Name='missing key';Reason='sync_key_missing';Local=@{KeyPresent=$false};Remote=@{}},
    @{Name='wrong URL';Reason='sync_url_requires_review';Local=@{ApprovedUrl=$false};Remote=@{}},
    @{Name='invalid local identity';Reason='installation_identity_invalid';Local=@{IdentityPresent=$false};Remote=@{}},
    @{Name='old protocol';Reason='sync_protocol_mismatch';Local=@{};Remote=@{protocolVersion=1}},
    @{Name='nonempty unbound mirror';Reason='source_enrollment_required';Local=@{};Remote=@{sourceId=$null}},
    @{Name='wrong owner';Reason='source_conflict';Local=@{};Remote=@{sourceId='22222222-2222-4222-8222-222222222222'}},
    @{Name='malformed owner';Reason='remote_identity_invalid';Local=@{};Remote=@{sourceId='invalid'}},
    @{Name='empty owner identity';Reason='remote_identity_invalid';Local=@{};Remote=@{sourceId='00000000-0000-0000-0000-000000000000'}},
    @{Name='old schema';Reason='schema_mismatch';Local=@{};Remote=@{schema='old'}},
    @{Name='remote behind acknowledgement';Reason='sync_reconciliation_required';Local=@{};Remote=@{seq=9}},
    @{Name='remote ahead of sent';Reason='sync_reconciliation_required';Local=@{};Remote=@{seq=13}},
    @{Name='invalid sequence';Reason='sync_state_invalid';Local=@{};Remote=@{seq='invalid'}},
    @{Name='missing sequence';Reason='sync_state_invalid';Local=@{};Remote=@{seq=$null}},
    @{Name='invalid local history';Reason='sync_state_invalid';Local=@{Acked=13L};Remote=@{}},
    @{Name='old queued request';Reason='old_commands_require_review';Local=@{};Remote=@{};Pending=1}
)
foreach($case in $cases){
    $l=$local.Clone();$r=$remote.Clone();foreach($k in $case.Local.Keys){$l[$k]=$case.Local[$k]};foreach($k in $case.Remote.Keys){$r[$k]=$case.Remote[$k]}
    $d=Get-SyncBootstrapDecision $l ([pscustomobject]$r) ([int]$case.Pending)
    if($d.Reason -ne $case.Reason -or $d.Ready -ne ($case.Reason -eq '')){throw "Decision failed: $($case.Name)"}
}

# Execute CURRENT entry source against simulated installed-host adapters. The
# administrator directive alone is omitted; no production bypass is introduced.
$stub=Join-Path $work 'adapter'
New-Item -ItemType Directory $stub|Out-Null
$entry=(Get-Content (Join-Path $repo 'deploy\windows\sync-bootstrap.ps1') -Raw) -replace '(?m)^#Requires -RunAsAdministrator\r?\n',''
[IO.File]::WriteAllText((Join-Path $stub 'sync-bootstrap.ps1'),$entry)
Copy-Item (Join-Path $repo 'deploy\windows\sync-bootstrap-common.ps1') $stub
Copy-Item (Join-Path $repo 'deploy\windows\sync-upgrade-common.ps1') $stub
$adapters=@'
function Protect-Path($Path){if(-not(Test-Path -LiteralPath $Path)){throw 'Unexpected path'}}
function Get-CimInstance($ClassName,$Filter){
 if($Filter -like '*AlShifaApi*'){[pscustomobject]@{PathName=(Join-Path $global:BootstrapRoot 'app\AlShifa.Api.exe')}}
 else{[pscustomobject]@{PathName=((Join-Path $global:BootstrapRoot 'mysql\bin\mysqld.exe')+' --defaults-file=synthetic.ini')}}
}
function Get-ItemProperty { [pscustomobject]@{Environment=@()} }
function Invoke-Native($Executable,[string[]]$Arguments){
 $query=$Arguments[[array]::IndexOf($Arguments,'-e')+1]
 if($query -eq 'SELECT @@port,@@datadir;'){return "33383`t$global:BootstrapRoot\data\"}
 if($query.StartsWith('SELECT (SELECT MigrationId')){return "20261122151006_OperationsAndStaffPayroll`t10`t12"}
 if($query.StartsWith('INSERT INTO SyncStates') -and $query -notmatch '(?i)DELETE|DROP|TRUNCATE'){$global:BootstrapWrites++;return}
 throw 'Unexpected SQL operation in synthetic bootstrap test'
}
function Invoke-WebRequest($Uri,$Method,$Headers,$MaximumRedirection,$TimeoutSec,[switch]$UseBasicParsing){
 $global:BootstrapRequests++
 if($MaximumRedirection -ne 0 -or ([uri]$Uri).Scheme -ne 'https'){throw 'Unsafe request'}
 $path=([uri]$Uri).AbsolutePath
 $sha=[Security.Cryptography.SHA256]::Create();$mac=New-Object Security.Cryptography.HMACSHA256
 try{
  $mac.Key=[Text.Encoding]::UTF8.GetBytes(('K'*40))
  $empty=([BitConverter]::ToString($sha.ComputeHash([byte[]]@()))).Replace('-','')
  $expected=([BitConverter]::ToString($mac.ComputeHash([Text.Encoding]::UTF8.GetBytes("$($Headers['X-Sync-Time'])`n$Method`n$path`n$empty")))).Replace('-','')
  if($Headers['X-Sync-Signature'] -cne $expected){throw 'HMAC interoperability mismatch'}
 }finally{$sha.Dispose();$mac.Dispose()}
 if($global:BootstrapCase -eq 'offline'){throw 'synthetic network failure; secret must never be serialized'}
 $remote=@{protocolVersion=2;sourceId='11111111-1111-4111-8111-111111111111';schema='20261122151006_OperationsAndStaffPayroll';seq=11}
 if($global:BootstrapCase -eq 'unbound'){$remote.sourceId=$null}
 if($global:BootstrapCase -eq 'wrong-owner'){$remote.sourceId='22222222-2222-4222-8222-222222222222'}
 $content=if($path.EndsWith('/pending')){if($global:BootstrapCase -eq 'old-command'){'[{"schema":"old"}]'}else{'[]'}}else{$remote|ConvertTo-Json}
 [pscustomobject]@{Content=$content}
}
'@
[IO.File]::WriteAllText((Join-Path $stub 'common.ps1'),$adapters)
foreach($name in @('ready','offline','unbound','wrong-owner','missing-key','old-command','established-offline','established-missing-identity')){
    $global:BootstrapCase=$name;$global:BootstrapRequests=0;$global:BootstrapWrites=0
    $global:BootstrapRoot=Join-Path $work $name
    foreach($dir in 'app','data','keys','logs','secrets'){New-Item -ItemType Directory -Force (Join-Path $global:BootstrapRoot $dir)|Out-Null}
    $sentinel=Join-Path $global:BootstrapRoot 'data\preserve-sentinel.txt';[IO.File]::WriteAllText($sentinel,'synthetic existing data')
    [IO.File]::WriteAllText((Join-Path $global:BootstrapRoot 'keys\sync-installation-id'),$source)
    $config=@{ConnectionStrings=@{Default='Server=127.0.0.1;Port=33383;Database=alshifa;User=synthetic;Password=synthetic-password'};DataProtection=@{KeysPath=(Join-Path $global:BootstrapRoot 'keys')};Mirror=@{Enabled=$false};Sync=@{RequireCompatibilityApproval=$true;IntervalMinutes=2;Key=$(if($name -eq 'missing-key'){''}else{'K'*40})};PreserveSetting='unchanged'}
    if($name.StartsWith('established-')){
        $config.Sync.RequireCompatibilityApproval=$false
        $config.Sync.Url='https://shafa.novatechsoft.com'
        if($name -eq 'established-missing-identity'){Remove-Item -LiteralPath (Join-Path $global:BootstrapRoot 'keys\sync-installation-id')}
    }
    $config|ConvertTo-Json -Depth 10|Set-Content (Join-Path $global:BootstrapRoot 'app\appsettings.Production.json') -Encoding UTF8
    & (Join-Path $stub 'sync-bootstrap.ps1') -Root $global:BootstrapRoot -NoRestart | Out-Null
    $statusText=Get-Content (Join-Path $global:BootstrapRoot 'logs\sync-bootstrap-status.json') -Raw
    $status=$statusText|ConvertFrom-Json
    $after=Get-Content (Join-Path $global:BootstrapRoot 'app\appsettings.Production.json') -Raw|ConvertFrom-Json
    if($status.Ready -ne ($name -in @('ready','established-offline')) -or $status.RealSyncAccepted){throw "Simulated installer failed: $name ($($status.Reason))"}
    if($after.PreserveSetting -ne 'unchanged' -or $after.Sync.IntervalMinutes -ne 2 -or (Get-Content $sentinel -Raw) -ne 'synthetic existing data'){throw 'Existing data/settings changed'}
    if($name -ne 'established-missing-identity' -and (Get-Content (Join-Path $global:BootstrapRoot 'keys\sync-installation-id') -Raw) -ne $source){throw 'Existing identity changed'}
    if($global:BootstrapWrites -ne $(if($name -eq 'ready'){1}else{0})){throw 'Unverified source was enabled'}
    if($name -eq 'ready' -and $after.Sync.RequireCompatibilityApproval){throw 'Verified source did not auto configure'}
    if($name -notin @('ready','established-offline','established-missing-identity') -and -not $after.Sync.RequireCompatibilityApproval){throw 'Safety hold was bypassed'}
    if($name.StartsWith('established-') -and $global:BootstrapRequests -ne 0){throw 'Established source contacted enrollment endpoints'}
    if($name -eq 'established-offline' -and ($after.Sync.RequireCompatibilityApproval -or $status.Reason -ne 'existing_configuration_preserved')){throw 'Established Sync was put back on hold'}
    if($name -eq 'established-missing-identity' -and (Test-Path -LiteralPath (Join-Path $global:BootstrapRoot 'keys\sync-installation-id'))){throw 'Missing established identity was silently recreated'}
    if($statusText.Contains('synthetic-password') -or $statusText.Contains(('K'*40)) -or $statusText.Contains($source) -or $statusText.Contains('synthetic network failure')){throw 'Sensitive error/detail leaked'}
    if(@(Get-ChildItem (Join-Path $global:BootstrapRoot 'secrets') -Filter '*.cnf').Count){throw 'Temporary client credential file retained'}
}
# Use actual Windows trigger/action constructors; intercept only registration/removal.
Import-Module ScheduledTasks
function Get-ScheduledTask { $global:FakeBootstrapTask }
function Register-ScheduledTask($TaskName,$Action,$Trigger,$Settings,$User,$RunLevel,[switch]$Force){
    $global:RegisteredBootstrapTask=@{Name=$TaskName;Action=$Action;Trigger=$Trigger;User=$User;RunLevel=$RunLevel}
}
function Unregister-ScheduledTask($TaskName,$Confirm){$global:RemovedBootstrapTask=$TaskName}
$global:FakeBootstrapTask=$null;$global:RegisteredBootstrapTask=$null
if((Get-Command Register-ScheduledTask).ScriptBlock.ToString() -notmatch 'RegisteredBootstrapTask'){throw 'Registration mock is not active'}
Set-SyncBootstrapTask $global:BootstrapRoot
if($global:RegisteredBootstrapTask.User -ne 'SYSTEM' -or $global:RegisteredBootstrapTask.RunLevel -ne 'Highest' -or $global:RegisteredBootstrapTask.Trigger.Count -ne 2 -or @($global:RegisteredBootstrapTask.Trigger|Where-Object {$_.Repetition.Interval -eq 'PT5M'}).Count -ne 1){throw 'Automatic startup/retry task configuration failed'}
$global:FakeBootstrapTask=[pscustomobject]@{Actions=@($global:RegisteredBootstrapTask.Action)}
[IO.File]::WriteAllText((Join-Path $global:BootstrapRoot 'logs\sync-bootstrap-status.json'),'{"Ready":true}')
Set-SyncBootstrapTask $global:BootstrapRoot
if($global:RemovedBootstrapTask -ne 'AlShifa Sync Bootstrap'){throw 'Completed one-time setup did not remove its retry task'}
$global:FakeBootstrapTask=[pscustomobject]@{Actions=@([pscustomobject]@{Arguments='unrelated task'})}
$refused=$false;try{Set-SyncBootstrapTask $global:BootstrapRoot}catch{$refused=$true}
if(-not $refused){throw 'Unrelated scheduled task was overwritten'}
$proof=@{decisionCases=$cases.Count;simulatedInstalledHostCases=8;realServicesTouched=$false;realDatabasesTouched=$false;realTasksRegistered=$false;actualWindowsTaskConstructorsVerified=$true;retryTaskLifecycleVerified=$true;networkMocked=$true;identityAndDataPreserved=$true;establishedOfflineSyncPreserved=$true;missingEstablishedIdentityNeverRecreated=$true;unverifiedSourcesHeld=$true;verifiedSourceAutomaticallyConfigured=$true;secretsAbsentFromStatus=$true;realHospitalSyncAccepted=$false}
$proof|ConvertTo-Json|Set-Content (Join-Path $work 'result.json') -Encoding UTF8
$proof|ConvertTo-Json
"Synthetic bootstrap evidence: $work"
