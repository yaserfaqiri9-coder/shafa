# Synthetic tool tests. No real Windows service, hospital configuration, or installed database is used.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$work=Join-Path $repo ('.tools\hospital-review-lab-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($work)
Add-Type -Path (Join-Path $repo 'deploy\windows\sync-review\ReviewCrypto.cs') -ReferencedAssemblies System.Core,System.Security,System.Web.Extensions
. (Join-Path $repo 'deploy\windows\sync-review\Review.Core.ps1')
Protect-ReviewDirectory $work
$root=Join-Path $work 'hospital';$package=Join-Path $work 'package';$returns=Join-Path $work 'returns'
foreach($p in $root,$package,$returns){[void][IO.Directory]::CreateDirectory($p)}
$checks=New-Object 'Collections.Generic.List[string]'
function Assert-ReviewTest([bool]$Condition,[string]$Name){if(-not $Condition){throw ('Synthetic check failed: '+$Name)};$checks.Add($Name)}
$officeKey=Join-Path $work 'office-private.dpapi';$public=[AlShifa.SyncReview.Crypto]::NewProtectedKey($officeKey)
[IO.File]::WriteAllText((Join-Path $package 'office-public.xml'),$public)
$plain=[Text.Encoding]::UTF8.GetBytes('synthetic unicode: آزمایش')
$sealed=[AlShifa.SyncReview.Crypto]::Seal($public,'synthetic-package',$plain)
$opened=[AlShifa.SyncReview.Crypto]::Open($officeKey,'synthetic-package',$sealed)
Assert-ReviewTest ([Text.Encoding]::UTF8.GetString($opened) -ceq [Text.Encoding]::UTF8.GetString($plain)) 'encrypted evidence roundtrip'
$tampered=$sealed|ConvertFrom-Json;$tampered.tag=('0'*64);$rejected=$false
try{[void][AlShifa.SyncReview.Crypto]::Open($officeKey,'synthetic-package',($tampered|ConvertTo-Json -Compress))}catch{$rejected=$true}
Assert-ReviewTest $rejected 'tampered evidence rejected'
$rejected=$false;try{[void][AlShifa.SyncReview.Crypto]::Open($officeKey,'other-package',$sealed)}catch{$rejected=$true}
Assert-ReviewTest $rejected 'wrong package rejected'
$otherKey=Join-Path $work 'wrong-private.dpapi';[void][AlShifa.SyncReview.Crypto]::NewProtectedKey($otherKey)
$rejected=$false;try{[void][AlShifa.SyncReview.Crypto]::Open($otherKey,'synthetic-package',$sealed)}catch{$rejected=$true}
Assert-ReviewTest $rejected 'wrong recipient rejected'
$echoSource=Join-Path $work 'ArgEcho.cs';$echo=Join-Path $work 'ArgEcho.exe'
[IO.File]::WriteAllText($echoSource,'using System;using System.Text;using System.Web.Script.Serialization;class Echo {static void Main(string[] args){Console.OutputEncoding=new UTF8Encoding(false);Console.Write(new JavaScriptSerializer().Serialize(args));}}')
& (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:exe ("/out:$echo") /reference:System.Web.Extensions.dll $echoSource
if($LASTEXITCODE -ne 0){throw 'Synthetic argument probe compilation failed'}
$native=[AlShifa.SyncReview.Native]::Run($echo,@('space value','trailing\','quoted"value','دری'),$null,(Join-Path $work 'args.err'),30000)
$arguments=$native.Output|ConvertFrom-Json
Assert-ReviewTest ($native.ExitCode -eq 0 -and $arguments.Count -eq 4 -and $arguments[0] -ceq 'space value' -and $arguments[1] -ceq 'trailing\' -and $arguments[2] -ceq 'quoted"value' -and $arguments[3] -ceq 'دری') 'native argument escaping preserves quotes Unicode spaces and trailing slash'
$runtime=Join-Path $repo '.tools\mysql-8.4.11-winx64'
$bin=Join-Path $root 'mysql\bin';[void][IO.Directory]::CreateDirectory($bin)
foreach($item in Get-ChildItem -LiteralPath (Join-Path $runtime 'bin') -File|Where-Object {$_.Extension -eq '.dll' -or $_.Name -in @('mysql.exe','mysqld.exe','mysqldump.exe')}){[IO.File]::Copy($item.FullName,(Join-Path $bin $item.Name))}
Copy-Item -LiteralPath (Join-Path $runtime 'share') -Destination (Join-Path $root 'mysql\share') -Recurse
foreach($folder in 'app','keys','secrets','storage'){[void][IO.Directory]::CreateDirectory((Join-Path $root $folder))}
$identity=[guid]::NewGuid().ToString('D');[IO.File]::WriteAllText((Join-Path $root 'keys\sync-installation-id'),$identity)
foreach($file in 'AlShifa.Api.exe','AlShifa.Api.dll'){[IO.File]::WriteAllText((Join-Path $root ('app\'+$file)),'synthetic fake application; never executed')}
$data=Join-Path $root 'data';$mysqld=Join-Path $bin 'mysqld.exe';$mysql=Join-Path $bin 'mysql.exe'
$portProbe=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$portProbe.Start();$port=([Net.IPEndPoint]$portProbe.LocalEndpoint).Port;$portProbe.Stop()
if($port -in @(3306,3307,3308)){throw 'Unsafe synthetic source port'}
$password=[AlShifa.SyncReview.Crypto]::Hash([AlShifa.SyncReview.Crypto]::Random(32));$cnf=Join-Path $work 'source-private.cnf';New-ReviewClientFile $cnf 'root' $password $port
$init=Join-Path $work 'init-private.sql';[IO.File]::WriteAllText($init,"ALTER USER 'root'@'localhost' IDENTIFIED BY '$password';")
[void](Invoke-ReviewNative $mysqld @('--no-defaults','--initialize-insecure',"--basedir=$(Join-Path $root 'mysql')","--datadir=$data",'--console') $work $null 180000)
$source=New-Object Diagnostics.Process;$source.StartInfo=[AlShifa.SyncReview.Native]::Info($mysqld,@('--no-defaults','--no-monitor',"--basedir=$(Join-Path $root 'mysql')","--datadir=$data",'--bind-address=127.0.0.1',"--port=$port",'--mysqlx=OFF','--skip-log-bin','--event-scheduler=OFF',"--init-file=$init",'--console'))
[void]$source.Start();$source.StandardInput.Close();$sourceOut=$source.StandardOutput.ReadToEndAsync();$sourceErr=$source.StandardError.ReadToEndAsync()
$proof=[ordered]@{passed=$false;synthetic=$true;realServicesTouched=$false;realHospitalDatabaseTouched=$false;checks=@();failureType=$null}
try {
    $ready=$false
    for($i=0;$i -lt 90;$i++){try{$v=Invoke-ReviewNative $mysql @("--defaults-file=$cnf",'-N','-B','-e','SELECT @@port,@@datadir;') $work $null 3000;$parts=$v.Trim() -split "`t";if([int]$parts[0] -eq $port -and [IO.Path]::GetFullPath($parts[1]).TrimEnd('\') -eq $data){$ready=$true;break}}catch{};Start-Sleep -Seconds 1}
    if(-not $ready){throw 'Dedicated synthetic database not ready'}
    $listeners=@(Get-NetTCPConnection -State Listen -LocalPort $port)
    Assert-ReviewTest (@($listeners|Where-Object {$_.LocalAddress -ne '127.0.0.1' -or $_.OwningProcess -ne $source.Id}).Count -eq 0) 'synthetic source PID port datadir and loopback verified'
    $seed=@'
CREATE DATABASE alshifa CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
USE alshifa;
CREATE TABLE __EFMigrationsHistory (MigrationId varchar(150) PRIMARY KEY,ProductVersion varchar(32));
INSERT INTO __EFMigrationsHistory VALUES ('20261122151006_OperationsAndStaffPayroll','10.0.0');
CREATE TABLE Patients (Id bigint PRIMARY KEY,Name varchar(100),Version int NOT NULL,DisplayName varchar(150) GENERATED ALWAYS AS (CONCAT(Name,'!')) STORED);
INSERT INTO Patients(Id,Name,Version) VALUES (1,'synthetic',1),(2,CONVERT(0xD8A2D8B2D985D8A7DB8CD8B4 USING utf8mb4),1);
CREATE TABLE Invoices (Id bigint PRIMARY KEY,PatientId bigint,Amount decimal(18,2),CONSTRAINT fk_invoice_patient FOREIGN KEY (PatientId) REFERENCES Patients(Id));
INSERT INTO Invoices VALUES (1,1,80.00);
CREATE TABLE Payments (Id bigint PRIMARY KEY,InvoiceId bigint,Amount decimal(18,2));
INSERT INTO Payments VALUES (1,1,60.00);
CREATE TABLE LedgerEntries (Id bigint PRIMARY KEY,TransactionId varchar(64),Debit decimal(18,2),Credit decimal(18,2));
INSERT INTO LedgerEntries VALUES (1,'synthetic',60,0),(2,'synthetic',0,60);
CREATE TABLE Attachments (Id bigint PRIMARY KEY,StoredName varchar(64),Sha256 varchar(64));
CREATE TABLE AspNetUsers (Id bigint PRIMARY KEY,UserName varchar(64),PasswordHash varchar(128),SecurityStamp varchar(64));
INSERT INTO AspNetUsers VALUES (1,'synthetic','synthetic-secret-sentinel','synthetic-stamp');
CREATE TABLE CompositeLinks (PatientId bigint,Label varchar(30),PRIMARY KEY (PatientId,Label));
INSERT INTO CompositeLinks VALUES (1,'synthetic'),(2,'second');
CREATE TABLE SyncStates (`Key` varchar(40) PRIMARY KEY,Value varchar(2000),UpdatedAt datetime(6));
CREATE TABLE SyncChanges (Id bigint PRIMARY KEY,TableName varchar(64),KeyJson varchar(300),CreatedAt datetime(6));
INSERT INTO SyncChanges VALUES (1,'Patients','[1]',UTC_TIMESTAMP(6)),(2,'Patients','[2]',UTC_TIMESTAMP(6)),(3,'Patients','[1]',UTC_TIMESTAMP(6)),(4,'CompositeLinks','[1,"synthetic"]',UTC_TIMESTAMP(6));
CREATE TABLE SyncCommands (Id varchar(64) PRIMARY KEY,RequestHash varchar(64),StatusCode int,CompletedAt datetime(6),BaseSeq bigint);
INSERT INTO SyncCommands VALUES (REPEAT('a',64),REPEAT('c',64),409,UTC_TIMESTAMP(6),0);
'@
    $seedFile=Join-Path $work 'synthetic-seed.sql';[IO.File]::WriteAllText($seedFile,$seed,(New-Object Text.UTF8Encoding($false)))
    [void](Invoke-ReviewNative $mysql @("--defaults-file=$cnf",'--binary-mode') $work $seedFile)
    $config=@{ConnectionStrings=@{Default="Server=127.0.0.1;Port=$port;Database=alshifa;User=root;Password=$password"};DataProtection=@{KeysPath=(Join-Path $root 'keys')};Mirror=@{Enabled=$false};Sync=@{RequireCompatibilityApproval=$true};Storage=@{Path=(Join-Path $root 'storage')};Kestrel=@{Endpoints=@{Loopback=@{Url='http://127.0.0.1:33499'}}}}
    $configPath=Join-Path $root 'app\appsettings.Production.json';[IO.File]::WriteAllText($configPath,($config|ConvertTo-Json -Depth 8))
    $manifest=@{packageId='synthetic-package';installationId=$identity;schema='20261122151006_OperationsAndStaffPayroll';receiptTargets=@(@{id=('a'*64)},@{id=('b'*64)});syncPolicy=@{modelTables=@('Patients','Invoices','Payments','LedgerEntries','Attachments','AspNetUsers','CompositeLinks','SyncStates','SyncChanges','SyncCommands');localOnlyTables=@('SyncStates','SyncChanges','SyncCommands');localOnlyColumns=@{AspNetUsers=@('PasswordHash','SecurityStamp')}}}
    $manifestPath=Join-Path $package 'package.json';[IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 10))
    $script:FakeRoot=$root;$script:FakeState='Running';$script:Stops=0;$script:Starts=0
    function Get-CimInstance($ClassName,$Filter){if($Filter -like '*AlShifaApi*'){[pscustomobject]@{PathName=('"'+(Join-Path $script:FakeRoot 'app\AlShifa.Api.exe')+'"');State=$script:FakeState}}else{[pscustomobject]@{PathName=('"'+(Join-Path $script:FakeRoot 'mysql\bin\mysqld.exe')+'" --synthetic');State='Running'}}}
    function Get-ItemProperty { [pscustomobject]@{Environment=@()} }
    function Stop-Service($Name){if($Name -ne 'AlShifaApi'){throw 'Unexpected service'};$script:Stops++;$script:FakeState='Stopped'}
    function Start-Service($Name){if($Name -ne 'AlShifaApi'){throw 'Unexpected service'};$script:Starts++;$script:FakeState='Running'}
    function Get-Service($Name){$s=[pscustomobject]@{Status=$script:FakeState};$s|Add-Member ScriptMethod WaitForStatus {param($Expected,$Timeout) if($script:FakeState -ne $Expected){throw 'Unexpected synthetic service state'}};return $s}
    function Invoke-WebRequest($Uri,[switch]$UseBasicParsing,$TimeoutSec){if($Uri -ne 'http://127.0.0.1:33499/health'){throw 'Unexpected synthetic API target'};return [pscustomobject]@{StatusCode=200;Content='Healthy'}}
    function Read-LatestEvidence {
        $file=Get-ChildItem -LiteralPath $returns -Filter '*.alshifa-review'|Sort-Object LastWriteTimeUtc -Descending|Select-Object -First 1
        return ([Text.Encoding]::UTF8.GetString([AlShifa.SyncReview.Crypto]::Open($officeKey,'synthetic-package',[IO.File]::ReadAllText($file.FullName)))|ConvertFrom-Json)
    }
    $layoutBefore=Get-ReviewLayout $mysql $cnf $work;$before=Get-ReviewProof $mysql $cnf $layoutBefore $work
    $configHash=(Get-FileHash -LiteralPath $configPath).Hash
    $run=@(Invoke-HospitalSyncReview $package $returns);$evidence=Read-LatestEvidence
    if(-not $evidence.success){[IO.File]::WriteAllText((Join-Path $work 'evidence-private.json'),($evidence|ConvertTo-Json -Depth 30));throw 'Normal synthetic review failed; inspect protected fixture evidence'}
    Assert-ReviewTest ($run[-1] -eq 0 -and $evidence.backupRestoreVerified -and $evidence.cloneServerIdentityVerified -and $evidence.privateCloneStopped) 'real backup restored in separate MySQL process and all tables matched'
    Assert-ReviewTest ($evidence.queueCount -eq 4 -and $evidence.pendingKeys.Count -eq 4 -and $evidence.localSequenceState.Count -eq 0) 'queue and missing sequence history collected without fabricated checkpoint'
    Assert-ReviewTest ($evidence.oldRequestReceipts[0].found -and -not $evidence.oldRequestReceipts[1].found -and -not $evidence.commandsReplayed) 'present and absent old receipts checked without replay'
    Assert-ReviewTest ($evidence.tables.Patients.columns -notcontains 'DisplayName' -and $evidence.tables.AspNetUsers.columns -notcontains 'PasswordHash' -and -not $evidence.tables.PSObject.Properties['SyncChanges']) 'sync policy excludes generated columns identity secrets and local-only tables'
    $pendingKey=$evidence.pendingKeys|Where-Object {$_.id -eq 1}|Select-Object -ExpandProperty key
    Assert-ReviewTest (@($evidence.tables.Patients.rows|Where-Object {$_.key -eq $pendingKey}).Count -eq 1) 'pending integer key fingerprints match source row fingerprints'
    $pendingComposite=$evidence.pendingKeys|Where-Object {$_.id -eq 4}|Select-Object -ExpandProperty key
    Assert-ReviewTest (@($evidence.tables.CompositeLinks.rows|Where-Object {$_.key -eq $pendingComposite}).Count -eq 1) 'pending composite key fingerprints match source row fingerprints'
    $after=Get-ReviewProof $mysql $cnf $layoutBefore $work
    Assert-ReviewTest (($before|ConvertTo-Json -Depth 10 -Compress) -ceq ($after|ConvertTo-Json -Depth 10 -Compress)) 'all synthetic source records queues receipts and history unchanged'
    Assert-ReviewTest ($script:Stops -eq 1 -and $script:Starts -eq 1 -and $script:FakeState -eq 'Running' -and (Get-FileHash -LiteralPath $configPath).Hash -eq $configHash) 'only simulated API paused and resumed and configuration preserved'
    Assert-ReviewTest $evidence.apiHealthyAfterRestart 'simulated API health verified after restart'
    Assert-ReviewTest (-not(Test-Path -LiteralPath (Join-Path $evidence.privateBackupPath 'source-temporary.cnf'))) 'temporary source credential removed'
    Assert-ReviewTest (-not $evidence.replicationInvoked -and -not $evidence.activationAuthorized -and -not $evidence.keyIssued -and -not $evidence.sourceIdChanged) 'no enrollment key issuance activation or replication'
    $envelopeText=[IO.File]::ReadAllText((Get-ChildItem -LiteralPath $returns -Filter '*.alshifa-review'|Select-Object -First 1).FullName)
    Assert-ReviewTest (-not $envelopeText.Contains('synthetic-secret-sentinel') -and -not $envelopeText.Contains($password)) 'return envelope contains no plaintext credentials'
    $manifest.installationId=[guid]::NewGuid().ToString('D');[IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 10))
    $stopCount=$script:Stops;[void]@(Invoke-HospitalSyncReview $package $returns);$bad=Read-LatestEvidence
    Assert-ReviewTest (-not $bad.success -and $script:Stops -eq $stopCount) 'wrong installation fails before service pause'
    $manifest.installationId=$identity;[IO.File]::WriteAllText($manifestPath,($manifest|ConvertTo-Json -Depth 10))
    $config.Sync.RequireCompatibilityApproval=$false;[IO.File]::WriteAllText($configPath,($config|ConvertTo-Json -Depth 8))
    [void]@(Invoke-HospitalSyncReview $package $returns);$bad=Read-LatestEvidence
    Assert-ReviewTest (-not $bad.success -and $script:Stops -eq $stopCount) 'missing hold fails before service pause'
    $config.Sync.RequireCompatibilityApproval=$true;[IO.File]::WriteAllText($configPath,($config|ConvertTo-Json -Depth 8))
    # Force a restore failure after a real isolated clone starts; the main API must already be resumed.
    $script:OriginalNative=(Get-Command Invoke-ReviewNative).ScriptBlock
    function Invoke-ReviewNative([string]$Exe,[string[]]$Arguments,[string]$Private,[string]$InputFile=$null,[int]$TimeoutMs=600000){if($InputFile -and (Split-Path $InputFile -Leaf) -eq 'database.sql'){throw 'synthetic_restore_failure'};& $script:OriginalNative $Exe $Arguments $Private $InputFile $TimeoutMs}
    [void]@(Invoke-HospitalSyncReview $package $returns);$bad=Read-LatestEvidence
    Assert-ReviewTest (-not $bad.success -and $bad.privateCloneStopped -and $bad.apiRestarted -and $script:FakeState -eq 'Running') 'restore failure stops owned clone and preserves simulated API availability'
    Set-Item -Path Function:Invoke-ReviewNative -Value $script:OriginalNative
    $after=Get-ReviewProof $mysql $cnf $layoutBefore $work
    Assert-ReviewTest (($before|ConvertTo-Json -Depth 10 -Compress) -ceq ($after|ConvertTo-Json -Depth 10 -Compress)) 'failed restore leaves every source row and queue unchanged'
    [void](Invoke-ReviewNative $mysql @("--defaults-file=$cnf",'alshifa','-e',"INSERT INTO SyncChanges VALUES (5,'UnknownSynthetic','[1]',UTC_TIMESTAMP(6));") $work)
    [void]@(Invoke-HospitalSyncReview $package $returns);$bad=Read-LatestEvidence
    $remaining=[int](@(Read-ReviewSql $mysql $cnf 'SELECT COUNT(*) FROM SyncChanges' $work)[0])
    Assert-ReviewTest (-not $bad.success -and $script:FakeState -eq 'Running' -and $remaining -eq 5) 'unknown queue table blocks review and all queue entries remain'
    $proof.passed=$true
} catch {
    $proof.failureType=$_.Exception.GetType().FullName
    [IO.File]::WriteAllText((Join-Path $work 'failure-private.txt'),$_.Exception.ToString())
} finally {
    if($source -and -not $source.HasExited){$source.Kill();$source.WaitForExit(30000)|Out-Null}
    if($sourceErr){[IO.File]::WriteAllText((Join-Path $work 'source-private.log'),$sourceErr.GetAwaiter().GetResult())}
    $proof.checks=@($checks);[IO.File]::WriteAllText((Join-Path $work 'result.json'),($proof|ConvertTo-Json -Depth 10))
    Write-Output (@{passed=$proof.passed;checkCount=$checks.Count;failureType=$proof.failureType;evidencePath=$work}|ConvertTo-Json -Compress)
}
if(-not $proof.passed){exit 1}
