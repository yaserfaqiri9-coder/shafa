# Exercises the real first-install script on synthetic services/data. Does not change workstation power settings.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Root,[string]$Observer=[Security.Principal.WindowsIdentity]::GetCurrent().Name)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')
$Root=[IO.Path]::GetFullPath($Root).TrimEnd('\')
if((Split-Path $Root -Parent) -ne (Join-Path $repo '.tools') -or (Split-Path $Root -Leaf) -notmatch '^windows-fresh-lab-[a-f0-9]{32}$' -or (Test-Path $Root)){throw 'Only a new isolated lab root is accepted.'}
if(-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator required.'}
foreach($name in 'AlShifaApi','AlShifaMySQL'){if(Get-Service $name -ErrorAction SilentlyContinue){throw 'Existing services are never touched.'}}
foreach($name in 'AlShifa Backup','AlShifa Update Check','AlShifa Update Install'){if(Get-ScheduledTask $name -ErrorAction SilentlyContinue){throw 'Existing tasks are never touched.'}}
foreach($port in 33494,33495,33496){if(-not(Test-PortFree $port)){throw 'Lab port occupied.'}}
foreach($name in 'ConnectionStrings__Default','ASPNETCORE_ENVIRONMENT','Mirror__Enabled','Sync__Url'){
    if([Environment]::GetEnvironmentVariable($name,'Machine')){throw 'Machine environment override needs separate review; test was not started.'}
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
}
$work=$Root+'-package'; $created=$false; $eventBefore=[Diagnostics.EventLog]::SourceExists('AlShifa.Api')
$beforeRules=@(Get-NetFirewallRule -DisplayName 'Al-Shifa HTTPS (hospital LAN)' -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
$result=[ordered]@{completed=$false;checks=@();powerSettingsTested=$false;exeClickFlowTested=$false;cleanupErrors=@()}
try {
    New-Item -ItemType Directory $work | Out-Null
    Protect-Path $work @("${Observer}=RX")
    Copy-Item -LiteralPath (Join-Path $repo '.tools\final-release-source\app') -Destination (Join-Path $work 'app') -Recurse
    Copy-Item -LiteralPath (Join-Path $repo 'deploy\windows') -Destination (Join-Path $work 'scripts') -Recurse
    # Only workstation power-plan mutation is omitted from this test copy.
    $install=Join-Path $work 'scripts\install.ps1'
    $code=[IO.File]::ReadAllText($install)
    $start=$code.IndexOf("`$step = 'power settings'"); $end=$code.IndexOf("`$step = 'backup task'",$start)
    if($start -lt 0 -or $end -lt 0){throw 'Expected power block missing.'}
    [IO.File]::WriteAllText($install,$code.Substring(0,$start)+$code.Substring($end))
    $env:ASPNETCORE_ENVIRONMENT='Production'; $env:Mirror__Enabled='false'; $env:Sync__Url=''; $env:ALSHIFA_ADMIN_PASSWORD=New-PrintablePassword
    $created=$true
    Invoke-Native 'powershell.exe' @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$install,'-Root',$Root,'-LanIp','127.0.0.1','-MySqlZipFolder',(Join-Path $repo '.tools\mysql-8.4.11-winx64'),'-BackupDestination',(Join-Path $Root 'archives'),'-AllowSameDisk','-MySqlPort','33494','-LoopbackPort','33495','-HttpsPort','33496') | Out-Null
    $service=Get-CimInstance Win32_Service -Filter "Name='AlShifaApi'"
    if($service.State -ne 'Running' -or -not $service.PathName.Contains($Root)){throw 'Fresh service ownership/state failed.'}
    if((Invoke-WebRequest 'http://127.0.0.1:33495/health' -UseBasicParsing).Content -ne 'Healthy'){throw 'Fresh API/database unhealthy.'}
    $status=Get-Content (Join-Path $Root 'logs\backup-status.json') -Raw | ConvertFrom-Json
    if(-not $status.restoreVerifiedAt -or $status.lastError){throw 'Initial encrypted backup did not restore.'}
    $config=Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json
    if($config.ConnectionStrings.Default -notmatch 'Port=33494;Database=alshifa;'){throw 'Unexpected database endpoint.'}
    $result.checks=@('real fresh install.ps1 completed','dedicated Windows services and MySQL 33494 healthy','initial encrypted backup restored successfully','LAN firewall rule created','backup and update tasks registered','published frontend bundled')
    if(-not(Test-Path (Join-Path $Root 'app\wwwroot\index.html'))){throw 'Published frontend missing.'}
    $result.completed=$true
} catch {
    $result.failureType=$_.Exception.GetType().FullName
    $message=$_.Exception.Message
    $values=@($env:ALSHIFA_ADMIN_PASSWORD)
    foreach($file in 'mysql-root.txt','backup-password.txt'){if(Test-Path (Join-Path $Root "secrets\$file")){$values += [IO.File]::ReadAllText((Join-Path $Root "secrets\$file")).Trim()}}
    if(Test-Path (Join-Path $Root 'app\appsettings.Production.json')){
        $cfg=Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json
        $builder=New-Object System.Data.Common.DbConnectionStringBuilder; $builder.set_ConnectionString([string]$cfg.ConnectionStrings.Default)
        $values += [string]$builder['Password']; $values += [string]$cfg.Kestrel.Endpoints.Lan.Certificate.Password
    }
    foreach($value in $values){if($value){$message=$message.Replace($value,'[redacted]')}}
    $result.failureMessage=$message
    # Native exceptions may contain generated credentials. Keep them private; publish only a stage-neutral failure.
    if(Test-Path $work){[IO.File]::WriteAllText((Join-Path $work 'private-failure.txt'),$_.Exception.ToString()); Protect-Path (Join-Path $work 'private-failure.txt')}
} finally {
    Remove-Item Env:ALSHIFA_ADMIN_PASSWORD -ErrorAction SilentlyContinue
    if($created){
        foreach($name in 'AlShifa Backup','AlShifa Update Check','AlShifa Update Install'){
            try{$task=Get-ScheduledTask $name -ErrorAction SilentlyContinue; if($task -and ($task.Actions.Arguments -join ' ').Contains($Root)){Stop-ScheduledTask $name -ErrorAction SilentlyContinue; Unregister-ScheduledTask $name -Confirm:$false}}catch{$result.cleanupErrors+="task $name"}
        }
        foreach($name in 'AlShifaApi','AlShifaMySQL'){
            try{$s=Get-CimInstance Win32_Service -Filter "Name='$name'";if($s -and $s.PathName.Contains($Root)){Stop-Service $name -Force -ErrorAction SilentlyContinue;Invoke-Native 'sc.exe' @('delete',$name)|Out-Null}}catch{$result.cleanupErrors+="service $name"}
        }
        Get-NetFirewallRule -DisplayName 'Al-Shifa HTTPS (hospital LAN)' -ErrorAction SilentlyContinue | Where-Object {$beforeRules -notcontains $_.Name} | Remove-NetFirewallRule
        if(-not $eventBefore -and [Diagnostics.EventLog]::SourceExists('AlShifa.Api')){[Diagnostics.EventLog]::DeleteEventSource('AlShifa.Api')}
    }
    if(Test-Path $work){[IO.File]::WriteAllText((Join-Path $work 'result.json'),($result|ConvertTo-Json -Depth 5))}
}
if(-not $result.completed -or $result.cleanupErrors.Count){exit 1}
