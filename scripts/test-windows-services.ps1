# Real Windows SCM/Task Scheduler test with synthetic data only. Default mode is read-only preflight.
[CmdletBinding()]
param([switch]$Run, [string]$Root, [string]$Observer = [Security.Principal.WindowsIdentity]::GetCurrent().Name, [string]$Published)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')
if (-not $Root) { $Root = Join-Path $repo ('.tools\windows-service-lab-' + [Guid]::NewGuid().ToString('N')) }
$Root = [IO.Path]::GetFullPath($Root).TrimEnd('\')
if ((Split-Path $Root -Parent) -ne (Join-Path $repo '.tools') -or (Split-Path $Root -Leaf) -notmatch '^windows-service-lab-[a-f0-9]{32}$') { throw 'Only a new private Windows service lab path is accepted.' }
foreach ($name in 'AlShifaApi','AlShifaMySQL') { if (Get-Service -Name $name -ErrorAction SilentlyContinue) { throw "Service $name exists; no changes are permitted." } }
foreach ($name in 'AlShifa Backup','AlShifa Update Check','AlShifa Update Install') { if (Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue) { throw "Task $name exists; no changes are permitted." } }
foreach ($port in 33490,33491,33492,33493) { if (-not (Test-PortFree $port)) { throw "Lab port $port is occupied." } }
if (Test-Path -LiteralPath $Root) { throw 'The test root must not already exist.' }
if(-not $Published){$Published = Join-Path $repo '.tools\windows-service-release\app'}
if (-not (Test-Path -LiteralPath (Join-Path $published 'AlShifa.Api.exe'))) { throw 'Publish the current win-x64 self-contained API before testing.' }
if (-not (Test-Path -LiteralPath (Join-Path $repo '.tools\windows-fault-fixture\AlShifa.Api.dll'))) { throw 'Build the isolated UpdateFault fixture before testing.' }
if (-not $Run) {
    "Preflight passed. Target: $Root"
    'Run mode temporarily creates only absent AlShifaApi/AlShifaMySQL services, three AlShifa scheduled tasks, and a localhost test certificate in LocalMachine My/Root.'
    'It uses MySQL 33490 and HTTP/HTTPS 33491-33493, synthetic accounts/patient/payment, actual event-triggered Backup, signed update success, migration failure and health failure rollback.'
    'Finally it removes only owned test services/tasks/certificate trust and stops the feed. Private test files and results are retained. XAMPP/mysql, ports 3307/3308, router and production are untouched.'
    return
}
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Actual service tests require an elevated Windows Administrator process.' }
$stage='initialize'; $ownedServices=@(); $ownedTasks=@(); $certificate=$null; $feed=$null; $eventSourceCreated=$false
$result=[ordered]@{ root=$Root; startedAt=[DateTime]::UtcNow.ToString('o'); passed=@(); completed=$false; failedStage=$null; cleanupErrors=@() }
function Pass([string]$Name) { $result.passed += $Name; [IO.File]::WriteAllText((Join-Path $Root 'progress.json'),(@{stage=$script:stage;passed=$result.passed} | ConvertTo-Json -Depth 5)); "PASS: $Name" }
function Query([string]$Sql) { Invoke-Native (Join-Path $Root 'mysql\bin\mysql.exe') @("--defaults-extra-file=$(Join-Path $Root 'secrets\root.cnf')",'-N','-B','--raw','alshifa','-e',$Sql) }
function Instrument-LabWorker([string]$Directory) {
    $path=Join-Path $Directory 'auto-update.ps1'
    $code=[IO.File]::ReadAllText($path)
    $code=$code.Replace("`$status.state = 'failed'", "[IO.File]::WriteAllText((Join-Path `$Root 'secrets\lab-update-error.txt'), `$_.Exception.ToString())`r`n    `$status.state = 'failed'")
    [IO.File]::WriteAllText($path,$code)
}
function Wait-Until([scriptblock]$Condition,[int]$Seconds=120) {
    $deadline=(Get-Date).AddSeconds($Seconds)
    do { if (& $Condition) { return }; Start-Sleep -Milliseconds 750 } while((Get-Date) -lt $deadline)
    throw "Timeout at stage $script:stage."
}
$session=New-Object Microsoft.PowerShell.Commands.WebRequestSession
function Api([string]$Path,[string]$Method='GET',$Body=$null) {
    $headers=@{}
    if ($Method -ne 'GET') {
        Invoke-WebRequest 'https://localhost:33493/api/auth/csrf' -WebSession $session -UseBasicParsing | Out-Null
        $token=$session.Cookies.GetCookies([uri]'https://localhost:33493')['XSRF-TOKEN'].Value
        $headers['X-XSRF-TOKEN']=[uri]::UnescapeDataString($token)
        $headers['Idempotency-Key']=[Guid]::NewGuid().ToString('N')
    }
    $arguments=@{Uri=('https://localhost:33493/api'+$Path);Method=$Method;WebSession=$session;Headers=$headers;UseBasicParsing=$true}
    if ($null -ne $Body) { $arguments.Body=ConvertTo-Json -InputObject $Body -Depth 10; $arguments.ContentType='application/json' }
    $reply=Invoke-WebRequest @arguments
    if ($reply.Content) { return ($reply.Content | ConvertFrom-Json) }
}
function Fingerprint {
    $sql="SELECT CONCAT(COUNT(*),':',COALESCE(SUM(Id),0),':',COALESCE(SUM(Amount),0)) FROM Payments; SELECT SHA2(GROUP_CONCAT(CONCAT(Id,':',UserName,':',PasswordHash,':',SecurityStamp) ORDER BY Id SEPARATOR '|'),256) FROM AspNetUsers; SELECT SHA2(GROUP_CONCAT(CONCAT(Id,':',FirstName,':',FileNumber) ORDER BY Id SEPARATOR '|'),256) FROM Patients; SELECT CONCAT(COALESCE(SUM(Debit),0),':',COALESCE(SUM(Credit),0)) FROM LedgerEntries;"
    $sql += " SELECT SHA2(GROUP_CONCAT(CONCAT(Id,':',Code,':',Name,':',SalePrice) ORDER BY Id),256) FROM Products; SELECT SHA2(GROUP_CONCAT(CONCAT(Id,':',ProductId,':',Quantity,':',UnitCost) ORDER BY Id),256) FROM StockBatches; SELECT SHA2(GROUP_CONCAT(CONCAT(Id,':',BatchId,':',Quantity,':',Kind) ORDER BY Id),256) FROM StockMovements;"
    return ((Query $sql) -join '|')
}
try {
    New-Item -ItemType Directory $Root | Out-Null
    Protect-Path $Root @("${Observer}=RX")
    [IO.File]::WriteAllText((Join-Path $Root 'fixture.marker'), (Split-Path $Root -Leaf))
    foreach ($folder in 'app','data','storage','keys','certs','logs','secrets','scripts','archives','backup-staging','feed','packages') { New-Item -ItemType Directory (Join-Path $Root $folder) | Out-Null }
    Protect-Path (Join-Path $Root 'secrets')
    Copy-Item -Path (Join-Path $published '*') -Destination (Join-Path $Root 'app') -Recurse
    Copy-Item -Path (Join-Path $repo 'deploy\windows\*') -Destination (Join-Path $Root 'scripts') -Recurse
    Instrument-LabWorker (Join-Path $Root 'scripts')
    Copy-Item -LiteralPath (Join-Path $repo '.tools\mysql-8.4.11-winx64') -Destination (Join-Path $Root 'mysql') -Recurse
    $bin=Join-Path $Root 'mysql\bin'; $ini=Join-Path $Root 'mysql\alshifa.ini'
    Get-MySqlIni (Join-Path $Root 'mysql') (Join-Path $Root 'data') (Join-Path $Root 'logs\mysql.err') 33490 | Set-Content -LiteralPath $ini -Encoding Ascii
    $rootPassword=New-Secret; $appPassword=New-Secret; $backupPassword=New-Secret; $restorePassword=New-Secret
    [IO.File]::WriteAllText((Join-Path $Root 'secrets\mysql-root.txt'),$rootPassword)
    Write-ClientCnf (Join-Path $Root 'secrets\root.cnf') 'root' $rootPassword 33490
    Write-ClientCnf (Join-Path $Root 'secrets\backup.cnf') 'alshifa_backup' $backupPassword 33490
    Write-ClientCnf (Join-Path $Root 'secrets\restore.cnf') 'alshifa_restore' $restorePassword 33490
    [IO.File]::WriteAllText((Join-Path $Root 'secrets\backup-password.txt'),(New-PrintablePassword))
    $bootstrap=Join-Path $Root 'secrets\bootstrap.sql'
    Get-MySqlBootstrapSql 'alshifa' $rootPassword $appPassword $backupPassword $restorePassword | Set-Content -LiteralPath $bootstrap -Encoding Ascii
    Invoke-Native (Join-Path $bin 'mysqld.exe') @("--defaults-file=$ini",'--initialize-insecure',"--init-file=$bootstrap") | Out-Null
    Remove-Item -LiteralPath $bootstrap -Force
    $stage='mysql-service'
    Invoke-Native (Join-Path $bin 'mysqld.exe') @('--install','AlShifaMySQL',"--defaults-file=$ini") | Out-Null
    $ownedServices += 'AlShifaMySQL'
    Invoke-Native 'sc.exe' @('config','AlShifaMySQL','obj=','NT SERVICE\AlShifaMySQL','start=','demand') | Out-Null
    Protect-Path (Join-Path $Root 'mysql') @('NT SERVICE\AlShifaMySQL=RX')
    Protect-Path (Join-Path $Root 'data') @('NT SERVICE\AlShifaMySQL=F')
    Protect-Path (Join-Path $Root 'logs') @('NT SERVICE\AlShifaMySQL=M',"${Observer}=R")
    Invoke-Native 'icacls.exe' @($Root,'/grant','NT SERVICE\AlShifaMySQL:(X)') | Out-Null
    Start-Service AlShifaMySQL
    Wait-Until { try { Query 'SELECT 1' | Out-Null; $true } catch { $false } }
    $identity=@(Query 'SELECT @@port, @@datadir;')[-1] -split "`t"
    if ([int]$identity[0] -ne 33490 -or [IO.Path]::GetFullPath($identity[1]).TrimEnd('\') -ne (Join-Path $Root 'data')) { throw 'Dedicated Windows database identity mismatch.' }
    Pass 'dedicated Windows MySQL service/port/data directory verified'
    $stage='certificate-and-api'
    $certificate=New-SelfSignedCertificate -DnsName 'localhost' -CertStoreLocation 'Cert:\LocalMachine\My' -NotAfter (Get-Date).AddDays(100) -FriendlyName (Split-Path $Root -Leaf) -KeyExportPolicy Exportable
    $store=New-Object Security.Cryptography.X509Certificates.X509Store('Root','LocalMachine')
    $store.Open('ReadWrite'); try { $store.Add($certificate) } finally { $store.Close() }
    $pfxPassword=New-Secret; $pfx=Join-Path $Root 'certs\lab.pfx'
    Export-PfxCertificate -Cert $certificate -FilePath $pfx -Password (ConvertTo-SecureString $pfxPassword -AsPlainText -Force) | Out-Null
    $config=@{ConnectionStrings=@{Default="Server=127.0.0.1;Port=33490;Database=alshifa;User=alshifa;Password=$appPassword;"};DataProtection=@{KeysPath=(Join-Path $Root 'keys')};Storage=@{Path=(Join-Path $Root 'storage')};Backup=@{StatusFile=(Join-Path $Root 'logs\backup-status.json')};Update=@{StatusFile=(Join-Path $Root 'logs\update-status.json');ApprovalFile=(Join-Path $Root 'requests\update-approval.json')};Kestrel=@{Endpoints=@{Loopback=@{Url='http://127.0.0.1:33492'};Lan=@{Url='https://127.0.0.1:33493';Certificate=@{Path=$pfx;Password=$pfxPassword}}}};Logging=@{LogLevel=@{Default='Warning'}}}
    [IO.File]::WriteAllText((Join-Path $Root 'app\appsettings.Production.json'),($config | ConvertTo-Json -Depth 16))
    [IO.File]::WriteAllText((Join-Path $Root 'app\release.json'),'{"version":"2026.11.21.1"}')
    $adminPassword='Lab-' + (New-Secret) + '-7a'
    $env:ASPNETCORE_ENVIRONMENT='Production'; $env:ConnectionStrings__Default=$config.ConnectionStrings.Default; $env:Mirror__Enabled='false'; $env:Sync__Url=''; $env:ALSHIFA_ADMIN_USER='synthetic_admin'; $env:ALSHIFA_ADMIN_PASSWORD=$adminPassword
    Push-Location (Join-Path $Root 'app')
    try { Invoke-Native (Join-Path $Root 'app\AlShifa.Api.exe') @('--create-admin') | Out-Null } finally { Pop-Location; Remove-Item Env:\ALSHIFA_ADMIN_PASSWORD,Env:\ALSHIFA_ADMIN_USER -ErrorAction SilentlyContinue }
    if (-not [Diagnostics.EventLog]::SourceExists('AlShifa.Api')) { [Diagnostics.EventLog]::CreateEventSource('AlShifa.Api','Application'); $eventSourceCreated=$true }
    Invoke-Native 'sc.exe' @('create','AlShifaApi','binPath=',(Join-Path $Root 'app\AlShifa.Api.exe'),'start=','demand','depend=','AlShifaMySQL','obj=','NT SERVICE\AlShifaApi') | Out-Null
    $ownedServices += 'AlShifaApi'
    $assembly=[Reflection.Assembly]::LoadFile((Join-Path $repo '.tools\upgrade-installer-stub.exe'))
    $hostType=$assembly.GetType('AlShifa.Setup.Host')
    if($hostType.GetField('Root').GetValue($null) -ne $Root -or $hostType.GetMethod('Detect').Invoke($null,@()).ToString() -ne 'Installed'){throw 'EXE failed to detect the existing lab installation.'}
    Pass 'actual EXE detection identifies installed services and their non-default installation root'
    New-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\AlShifaApi' -Name Environment -PropertyType MultiString -Value @('ASPNETCORE_ENVIRONMENT=Production',("ConnectionStrings__Default="+$config.ConnectionStrings.Default),'Mirror__Enabled=false','Sync__Url=') -Force | Out-Null
    Invoke-Native 'icacls.exe' @($Root,'/grant','NT SERVICE\AlShifaApi:(X)') | Out-Null
    Invoke-Native 'icacls.exe' @((Join-Path $Root 'logs'),'/grant','NT SERVICE\AlShifaApi:(OI)(CI)R') | Out-Null
    Protect-Path (Join-Path $Root 'app') @('NT SERVICE\AlShifaApi=RX')
    Protect-Path (Join-Path $Root 'certs') @('NT SERVICE\AlShifaApi=R')
    foreach ($folder in 'keys','storage') { Protect-Path (Join-Path $Root $folder) @('NT SERVICE\AlShifaApi=M') }
    Start-Service AlShifaApi
    Wait-Until { try { (Invoke-WebRequest 'http://127.0.0.1:33492/health' -UseBasicParsing).Content -eq 'Healthy' } catch { $false } }
    Api '/auth/login' 'POST' @{userName='synthetic_admin';password=$adminPassword} | Out-Null
    $patient=Api '/patients' 'POST' @{firstName='Synthetic Windows preservation';gender='Male';estimatedAge=30}
    $service=Api '/services' 'POST' @{name='Synthetic Windows service';isActive=$true;price=100}
    $register=Api '/cash/registers' 'POST' @{name='Synthetic Windows register'}
    Api '/cash/sessions' 'POST' @{registerId=$register.id;openingAmount=0} | Out-Null
    $invoice=Api '/invoices' 'POST' @{patientId=$patient.id;items=@(@{serviceId=$service.id;quantity=1})}
    Api '/payments' 'POST' @{invoiceId=$invoice.id;amount=60;method='Cash'} | Out-Null
    Query "INSERT INTO Products (Id,Code,Name,Kind,Unit,ReorderLevel,SalePrice,IsActive,Version) VALUES (900,'lab-drug-900','Synthetic preservation drug','Drug','unit',2,10,1,0); INSERT INTO StockLocations (Id,Code,Name) VALUES (900,'lab-900','Synthetic stock'); INSERT INTO StockBatches (Id,ProductId,LocationId,LotNumber,ExpiryDate,Quantity,UnitCost) VALUES (900,900,900,'synthetic-lot','2030-01-01',37,4); INSERT INTO StockMovements (Id,BatchId,Kind,Quantity,SourceType,SourceId,Reason,UserId,CreatedAt) SELECT 900,900,'Adjustment',37,'Lab',900,'Synthetic opening stock',Id,UTC_TIMESTAMP(6) FROM AspNetUsers WHERE UserName='synthetic_admin';" | Out-Null
    [IO.File]::WriteAllText((Join-Path $Root 'storage\synthetic-preservation.txt'),'synthetic attachment preservation')
    $stage='backup-failure-barrier'
    $blockedDestination=Join-Path $Root 'blocked-backup-destination'
    [IO.File]::WriteAllText($blockedDestination,'synthetic file blocking a directory destination')
    $beforeFailure=Fingerprint
    $refused=$false
    try{Invoke-Native 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $Root 'scripts\update.ps1'),'-Root',$Root,'-BackupDestination',$blockedDestination,'-AllowSameDisk') | Out-Null}catch{$refused=$true}
    if(-not $refused -or (Fingerprint) -ne $beforeFailure -or (Get-Service AlShifaApi).Status -ne 'Running' -or (Test-Path (Join-Path $Root 'keys\update-maintenance'))){throw 'A failed backup did not leave the old data/application running unchanged.'}
    if((Get-Content (Join-Path $Root 'app\release.json') -Raw | ConvertFrom-Json).version -ne '2026.11.21.1'){throw 'Failed backup changed the application version.'}
    Pass 'backup failure stops upgrade before live migration/file swap; previous database/application remain unchanged and running'
    Pass 'real Windows API service, trusted localhost HTTPS and synthetic user/patient/payment created'
    $stage='manual-backup'
    $taskArguments="-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $Root 'scripts\backup.ps1')`" -Root `"$Root`" -Destination `"$(Join-Path $Root 'archives')`" -AllowSameDisk"
    Register-ScheduledTask -TaskName 'AlShifa Backup' -Action (New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $taskArguments) -Trigger (New-KabulBackupTrigger) -Settings (New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable) -User 'SYSTEM' -RunLevel Highest | Out-Null
    $ownedTasks += 'AlShifa Backup'; Add-BackupRequestTrigger
    Set-AutoUpdateTasks $Root; $ownedTasks += 'AlShifa Update Check','AlShifa Update Install'
    $requested=[DateTime]::UtcNow
    Api '/settings/backup/run' 'POST' | Out-Null
    Wait-Until { try { $s=Get-Content (Join-Path $Root 'logs\backup-status.json') -Raw | ConvertFrom-Json; $s.lastSuccessAt -and [DateTime]$s.lastSuccessAt -ge $requested -and $s.restoreVerifiedAt -and -not $s.lastError } catch { $false } }
    Pass 'API manual backup event started SYSTEM task and the encrypted archive restored successfully'
    $stage='nightly-backup-task'
    $nightly=Get-ScheduledTask -TaskName 'AlShifa Backup'
    $boundary=[DateTimeOffset]::Parse(($nightly.Triggers | Where-Object {$_.CimClass.CimClassName -eq 'MSFT_TaskDailyTrigger'}).StartBoundary)
    if($boundary.Hour -ne 2 -or $boundary.Minute -ne 0 -or $boundary.Offset -ne [TimeSpan]::FromMinutes(270)){throw 'Nightly task is not set to Kabul 02:00.'}
    $priorBackup=(Get-Content (Join-Path $Root 'logs\backup-status.json') -Raw | ConvertFrom-Json).lastSuccessAt
    Wait-Until {(Get-ScheduledTask 'AlShifa Backup').State -ne 'Running'}
    Start-ScheduledTask 'AlShifa Backup'
    Wait-Until {try{$b=Get-Content (Join-Path $Root 'logs\backup-status.json') -Raw | ConvertFrom-Json; $b.lastSuccessAt -ne $priorBackup -and $b.restoreVerifiedAt -and -not $b.lastError}catch{$false}} 180
    Pass 'nightly SYSTEM task executed and restore verified; daily trigger is Kabul 02:00 (clock firing not awaited)'
    $baseline=Fingerprint
    $stage='signed-feed'
    $rsa=New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
    try { [IO.File]::WriteAllText((Join-Path $Root 'secrets\publisher-private.xml'),$rsa.ToXmlString($true)); [IO.File]::WriteAllText((Join-Path $Root 'secrets\update-public.xml'),$rsa.ToXmlString($false)) } finally { $rsa.Dispose() }
    [IO.File]::WriteAllText((Join-Path $Root 'secrets\auto-update.json'),(@{manifestUrl='https://localhost:33491/manifest.json';backupDestination=(Join-Path $Root 'archives');allowSameDisk=$true} | ConvertTo-Json))
    [IO.File]::WriteAllText((Join-Path $Root 'secrets\feed.json'),(@{pfx=$pfx;password=$pfxPassword;root=(Join-Path $Root 'feed')} | ConvertTo-Json))
    $feedScript=Join-Path $Root 'feed-server.cjs'
    [IO.File]::WriteAllText($feedScript, @'
const fs=require('fs'),https=require('https'),path=require('path');
const c=JSON.parse(fs.readFileSync(process.argv[2],'utf8'));
https.createServer({pfx:fs.readFileSync(c.pfx),passphrase:c.password},(q,r)=>{
 const name=decodeURIComponent(q.url.slice(1));
 if(!/^(manifest\.json(?:\.sig)?|alshifa-[0-9.]+\.zip)$/.test(name)){r.writeHead(404);r.end();return;}
 const p=path.join(c.root,name);if(!fs.existsSync(p)){r.writeHead(404);r.end();return;}
 r.writeHead(200,{'content-length':fs.statSync(p).size});fs.createReadStream(p).pipe(r);
}).listen(33491,'127.0.0.1');
'@)
    $feed=Start-Process -FilePath (Get-Command node.exe).Source -ArgumentList @("`"$feedScript`"","`"$(Join-Path $Root 'secrets\feed.json')`"") -WindowStyle Hidden -PassThru
    foreach ($version in '2026.11.21.2','2026.11.21.3','2026.11.21.4') {
        $stage='update-'+$version
        $release=Join-Path $Root ('packages\release-'+$version)
        New-Item -ItemType Directory -Path $release | Out-Null
        Copy-Item -LiteralPath $published -Destination (Join-Path $release 'app') -Recurse
        Copy-Item -LiteralPath (Join-Path $repo 'deploy\windows') -Destination (Join-Path $release 'scripts') -Recurse
        Instrument-LabWorker (Join-Path $release 'scripts')
        [IO.File]::WriteAllText((Join-Path $release 'app\release.json'),(@{version=$version} | ConvertTo-Json))
        if ($version -ne '2026.11.21.2') {
            # A test-only migration executable mutates only the verified synthetic database, then fails migration or service startup.
            Copy-Item -LiteralPath (Join-Path $repo '.tools\windows-fault-fixture\AlShifa.Api.dll') -Destination (Join-Path $release 'app\AlShifa.Api.dll') -Force
            [IO.File]::WriteAllText((Join-Path $release 'app\lab-fault.json'),(@{phase=$(if($version -eq '2026.11.21.3'){'migration'}else{'health'})} | ConvertTo-Json))
        }
        $signed=Join-Path $release 'signed'
        # Output stays outside the archive input tree.
        $signed=Join-Path $Root ('packages\signed-'+$version)
        & (Join-Path $repo 'deploy\windows\sign-update.ps1') -Release $release -PrivateKeyPath (Join-Path $Root 'secrets\publisher-private.xml') -Output $signed -PackageUrl "https://localhost:33491/alshifa-$version.zip" | Out-Null
        Copy-Item -Path (Join-Path $signed '*') -Destination (Join-Path $Root 'feed') -Force
        Start-ScheduledTask 'AlShifa Update Check'
        Wait-Until { try {$s=Get-Content (Join-Path $Root 'logs\update-status.json') -Raw | ConvertFrom-Json; $s.state -eq 'awaitingApproval' -and $s.availableVersion -eq $version} catch {$false} }
        Api '/settings/software-update/approve' 'POST' @{version=$version} | Out-Null
        Wait-Until { try {$s=Get-Content (Join-Path $Root 'logs\update-status.json') -Raw | ConvertFrom-Json; $s.availableVersion -eq $version -and $s.state -in @('installed','failed')} catch {$false} } 240
        $s=Get-Content (Join-Path $Root 'logs\update-status.json') -Raw | ConvertFrom-Json
        if (($version -eq '2026.11.21.2' -and $s.state -ne 'installed') -or ($version -ne '2026.11.21.2' -and $s.state -ne 'failed')) { throw 'Unexpected updater outcome.' }
        Wait-Until { try {(Invoke-WebRequest 'http://127.0.0.1:33492/health' -UseBasicParsing).Content -eq 'Healthy'} catch {$false} }
        if ((Fingerprint) -ne $baseline) { throw 'Synthetic account/patient/payment/ledger fingerprint changed.' }
        if ([int](Query "SELECT COUNT(*) FROM information_schema.columns WHERE table_schema='alshifa' AND table_name='Patients' AND column_name='LabRollbackProbe'") -ne 0) { throw 'Rollback did not remove the test migration column.' }
        $session=New-Object Microsoft.PowerShell.Commands.WebRequestSession
        Api '/auth/login' 'POST' @{userName='synthetic_admin';password=$adminPassword} | Out-Null
        if ((Get-Content (Join-Path $Root 'app\release.json') -Raw | ConvertFrom-Json).version -ne '2026.11.21.2') { throw 'Unexpected installed version after update/rollback.' }
        if($version -ne '2026.11.21.2'){
            if([IO.File]::ReadAllText((Join-Path $Root 'secrets\lab-update-error.txt')) -notmatch 'compatibility/preservation\s+rehearsal failed'){throw 'Fault was not refused by the pre-update copy rehearsal.'}
            Pass ("signed update ${version}: unsafe candidate refused on restored copy before live migration; drug/stock/patient/payment/accounts preserved")
        }else{Pass ("signed update ${version}: restored-copy rehearsal and update passed; drug/stock/patient/payment/accounts preserved")}
    }
    $stage='offline-upgrade-and-manual-backup-repair'
    $task=Get-ScheduledTask 'AlShifa Backup'
    Set-ScheduledTask -TaskName 'AlShifa Backup' -Trigger @($task.Triggers | Where-Object {$_.CimClass.CimClassName -ne 'MSFT_TaskEventTrigger'}) | Out-Null
    if($eventSourceCreated){[Diagnostics.EventLog]::DeleteEventSource('AlShifa.Api')}
    $offlineRelease=Join-Path $Root 'packages\release-2026.11.21.2'
    Invoke-Native 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $offlineRelease 'scripts\update.ps1'),'-Root',$Root,'-BackupDestination',(Join-Path $Root 'archives'),'-AllowSameDisk','-HoldOnlineServices') | Out-Null
    if((Fingerprint) -ne $baseline){throw 'Offline upgrade changed protected data.'}
    if(-not (Get-Content (Join-Path $Root 'app\appsettings.Production.json') -Raw | ConvertFrom-Json).Sync.RequireCompatibilityApproval){throw 'Offline installer did not hold online services.'}
    Invoke-Native 'powershell.exe' @('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $Root 'scripts\auto-update.ps1'),'-Root',$Root) | Out-Null
    if((Get-Content (Join-Path $Root 'logs\update-status.json') -Raw | ConvertFrom-Json).state -ne 'compatibilityReviewRequired'){throw 'Auto Update bypassed the compatibility hold.'}
    $session=New-Object Microsoft.PowerShell.Commands.WebRequestSession
    Api '/auth/login' 'POST' @{userName='synthetic_admin';password=$adminPassword} | Out-Null
    $requested=[DateTime]::UtcNow
    Api '/settings/backup/run' 'POST' | Out-Null
    Wait-Until {try{$s=Get-Content (Join-Path $Root 'logs\backup-status.json') -Raw | ConvertFrom-Json; [DateTime]$s.lastSuccessAt -ge $requested -and $s.restoreVerifiedAt -and -not $s.lastError}catch{$false}} 180
    Pass 'offline upgrade preserved data, held Sync/Auto Update and repaired the missing manual-backup event source/trigger; API backup restored successfully'
    $result.completed=$true
} catch {
    $result.failedStage=$stage
    # Keep process/SQL output and credentials out of the public result; private task/event evidence is retained.
    $result.failureType=$_.Exception.GetType().FullName
    $message=$_.Exception.Message
    $diagnostic=Join-Path $Root 'secrets\lab-update-error.txt'
    if(Test-Path -LiteralPath $diagnostic){$message += ' Details: ' + [IO.File]::ReadAllText($diagnostic)}
    foreach($secretName in 'rootPassword','appPassword','backupPassword','restorePassword','pfxPassword','adminPassword') {
        $secretValue=Get-Variable -Name $secretName -ValueOnly -ErrorAction SilentlyContinue
        if($secretValue){$message=$message.Replace([string]$secretValue,'[redacted]')}
    }
    $result.failureMessage=$message
    Write-Error "Windows service lab failed at stage $stage; inspect the protected lab evidence." -ErrorAction Continue
} finally {
    foreach ($name in $ownedTasks) { try { Stop-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue; Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction Stop } catch {$result.cleanupErrors += "task: $name"} }
    foreach ($name in @('AlShifaApi','AlShifaMySQL') | Where-Object {$ownedServices -contains $_}) { try { $service=Get-CimInstance Win32_Service -Filter "Name='$name'"; if($service -and $service.PathName.Contains($Root)){Stop-Service $name -Force -ErrorAction SilentlyContinue; Invoke-Native 'sc.exe' @('delete',$name) | Out-Null} else {throw 'Service ownership mismatch.'} } catch {$result.cleanupErrors += "service: $name"} }
    if ($feed -and -not $feed.HasExited) { Stop-Process -Id $feed.Id }
    if ($certificate) {
        foreach ($storeName in 'Root','My') { try { $store=New-Object Security.Cryptography.X509Certificates.X509Store($storeName,'LocalMachine'); $store.Open('ReadWrite'); try {$match=$store.Certificates.Find('FindByThumbprint',$certificate.Thumbprint,$false); foreach($item in $match){$store.Remove($item)}} finally {$store.Close()} } catch {$result.cleanupErrors += "certificate: $storeName"} }
    }
    if ($eventSourceCreated) { try {[Diagnostics.EventLog]::DeleteEventSource('AlShifa.Api')} catch {$result.cleanupErrors += 'event source'} }
    if(Test-Path -LiteralPath $Root){[IO.File]::WriteAllText((Join-Path $Root 'result.json'),($result | ConvertTo-Json -Depth 8))}
}
if (-not $result.completed -or $result.cleanupErrors.Count) { exit 1 }
