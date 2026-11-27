# Only a fresh, verified loopback instance. Never reads installed configuration or kills a port owner.
param([Parameter(Mandatory=$true)][string]$ApplicationPath,[ValidateRange(10,86400)][int]$Seconds=120,[ValidateRange(1,128)][int]$Users=16,[ValidateRange(5000,300000)][int]$Patients=50000,[ValidatePattern('^[a-zA-Z0-9-]+$')][string]$Label='measurement')
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'deploy/windows/common.ps1')
$app=[IO.Path]::GetFullPath($ApplicationPath)
if(-not $app.StartsWith((Join-Path $repo '.artifacts\'),[StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path (Join-Path $app 'AlShifa.Api.exe'))){throw 'Pass an explicitly built application inside repository .artifacts.'}
$mutex=New-Object Threading.Mutex($false,'Local\AlShifa.IsolatedVerification')
try{$owns=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$owns=$true}
if(-not $owns){$mutex.Dispose();throw 'Another verification lab owns the safety lock.'}
$mysqlProcess=$null;$api=$null;$load=$null
$lab=Join-Path $repo ('.artifacts\load-'+$Label+'-'+[Guid]::NewGuid().ToString('N'))
try{
    if(Get-Process testhost -ErrorAction SilentlyContinue){throw 'A testhost is active.'}
    New-Item -ItemType Directory $lab | Out-Null
    Protect-Path $lab @("$([Security.Principal.WindowsIdentity]::GetCurrent().Name)=F")
    $bin=Join-Path $repo '.tools/mysql-8.4.11-winx64/bin'
    $port=Select-FreePort @(33379..33399) 'dedicated load database'
    $apiPort=Select-FreePort @(33450..33499) 'dedicated load API'
    $db='alshifa_perf_load';$rootPassword=New-Secret;$appPassword=New-Secret
    $data=Join-Path $lab 'data';$ini=Join-Path $lab 'mysql.ini';$init=Join-Path $lab 'initialize.sql';$cnf=Join-Path $lab 'root.cnf'
    $settings=(Get-MySqlIni (Split-Path $bin -Parent) $data (Join-Path $lab 'mysql.err') $port).Replace('innodb_buffer_pool_size=512M','innodb_buffer_pool_size=256M')
    $settings+="`nslow_query_log=ON`nlong_query_time=0.05`nslow_query_log_file=$($lab.Replace('\','/'))/slow.log`n"
    $settings | Set-Content -LiteralPath $ini -Encoding Ascii
    Get-MySqlBootstrapSql $db $rootPassword $appPassword (New-Secret) (New-Secret) | Set-Content -LiteralPath $init -Encoding Ascii
    Write-ClientCnf $cnf 'root' $rootPassword $port
    Invoke-Native (Join-Path $bin 'mysqld.exe') @("--defaults-file=$ini",'--initialize-insecure') | Out-Null
    $mysqlProcess=Start-Process (Join-Path $bin 'mysqld.exe') -ArgumentList @("--defaults-file=`"$ini`"","--init-file=`"$init`"") -WindowStyle Hidden -PassThru
    $verified=$false
    for($i=0;$i -lt 90;$i++){
        if($mysqlProcess.HasExited){throw 'Dedicated MySQL exited.'}
        try{
            $identity=@(Invoke-Native (Join-Path $bin 'mysql.exe') @("--defaults-extra-file=$cnf",'-N','-B','-e','SELECT @@port,@@datadir;'))
            $parts=$identity[-1] -split "`t"
            if([int]$parts[0] -ne $port -or [IO.Path]::GetFullPath($parts[1]).TrimEnd('\') -ne [IO.Path]::GetFullPath($data).TrimEnd('\')){throw 'Wrong database identity.'}
            $listener=Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop
            $owner=Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
            if($listener.LocalAddress -ne '127.0.0.1' -or $owner.ExecutablePath -ne (Join-Path $bin 'mysqld.exe') -or -not $owner.CommandLine.Contains($ini)){throw 'Wrong database owner.'}
            $mysqlOwner=Get-Process -Id $listener.OwningProcess;$mysqlOwner.ProcessorAffinity=[IntPtr]15
            $verified=$true;break
        }catch{Start-Sleep -Milliseconds 500}
    }
    if(-not $verified){throw 'Dedicated database identity not verified.'}
    Remove-Item -LiteralPath $init -Force
    $grants=@(Invoke-Native (Join-Path $bin 'mysql.exe') @("--defaults-extra-file=$cnf",'-N','-B','-e',"SHOW GRANTS FOR 'alshifa'@'localhost';"))
    if(@($grants | Where-Object {$_ -match 'GRANT ALL' -and $_ -notmatch 'alshifa_perf_load'}).Count){throw 'Unexpected application privileges.'}
    $base="http://127.0.0.1:$apiPort";$adminPassword='Perf1!'+(New-Secret)
    $environment=@{ASPNETCORE_ENVIRONMENT='LoadTest';DOTNET_ENVIRONMENT='LoadTest';ASPNETCORE_URLS=$base;ConnectionStrings__Default="Server=127.0.0.1;Port=$port;Database=$db;User=alshifa;Password=$appPassword;";Mirror__Enabled='false';Sync__Url='';Sync__Key='';DataProtection__KeysPath=(Join-Path $lab 'keys');Storage__Path=(Join-Path $lab 'storage');ALSHIFA_ADMIN_USER='perf_operator';ALSHIFA_ADMIN_PASSWORD=$adminPassword;DOTNET_GCHeapHardLimit='20000000';DOTNET_PROCESSOR_COUNT='4';RateLimit__LoginPerMinute='100000';Logging__LogLevel__Default='Warning';Logging__LogLevel__Microsoft='Warning'}
    function Start-LabApi([string]$Arguments){
        $psi=New-Object Diagnostics.ProcessStartInfo
        $psi.FileName=Join-Path $app 'AlShifa.Api.exe';$psi.WorkingDirectory=$lab;$psi.Arguments=$Arguments
        $psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.RedirectStandardOutput=$true;$psi.RedirectStandardError=$true
        foreach($key in @($psi.EnvironmentVariables.Keys)){if($key -match '^(ConnectionStrings__|Kestrel__|Mirror__|Sync__|Update__|ASPNETCORE_|DOTNET_|ALSHIFA_|Storage__|DataProtection__)'){$psi.EnvironmentVariables.Remove($key)}}
        foreach($key in $environment.Keys){$psi.EnvironmentVariables[$key]=[string]$environment[$key]}
        $proc=New-Object Diagnostics.Process;$proc.StartInfo=$psi;[void]$proc.Start();$proc.ProcessorAffinity=[IntPtr]15
        return @{Process=$proc;Stdout=$proc.StandardOutput.ReadToEndAsync();Stderr=$proc.StandardError.ReadToEndAsync()}
    }
    $setup=Start-LabApi '--create-admin'
    if(-not $setup.Process.WaitForExit(180000)){$setup.Process.Kill();throw 'Schema initialization timed out.'}
    [IO.File]::WriteAllText((Join-Path $lab 'setup.log'),$setup.Stdout.Result+$setup.Stderr.Result)
    if($setup.Process.ExitCode -ne 0){throw 'Synthetic initialization failed; inspect private setup.log.'}
    $config=@{base=$base;user='perf_operator';password=$adminPassword;seconds=$Seconds;users=$Users;patients=$Patients;mysql=(Join-Path $bin 'mysql.exe');cnf=$cnf;database=$db;output=$lab}
    $configPath=Join-Path $lab 'config-private.json'
    [IO.File]::WriteAllText($configPath,($config | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    & node.exe (Join-Path $PSScriptRoot 'benchmark.mjs') $configPath seed
    if($LASTEXITCODE -ne 0){throw 'Synthetic fixture creation failed.'}
    $api=Start-LabApi '';$ready=$false
    for($i=0;$i -lt 90;$i++){
        if($api.Process.HasExited){throw 'Load API exited.'}
        try{if((Invoke-WebRequest "$base/health" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200){$ready=$true;break}}catch{Start-Sleep -Milliseconds 500}
    }
    if(-not $ready){throw 'Synthetic API not healthy.'}
    $apiListener=Get-NetTCPConnection -State Listen -LocalPort $apiPort
    if($apiListener.OwningProcess -ne $api.Process.Id -or $apiListener.LocalAddress -ne '127.0.0.1'){throw 'Wrong API owner.'}
    @{verified=$true;databasePort=$port;apiPort=$apiPort;database=$db;freshDatadir=$true;applicationGrantConfined=$true;fourLogicalProcessors=$true;testGcHeapLimitMiB=512;testRuntimeProcessorCount=4;testLoginLimitPerMinute=100000;testBufferPoolMiB=256;applicationSha256=(Get-FileHash (Join-Path $app 'AlShifa.Api.dll')).Hash;seconds=$Seconds;users=$Users;patients=$Patients} | ConvertTo-Json | Set-Content (Join-Path $lab 'isolation.json')
    $load=Start-Process node.exe -ArgumentList @("`"$(Join-Path $PSScriptRoot 'benchmark.mjs')`"","`"$configPath`"",'run') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $lab 'load.log') -RedirectStandardError (Join-Path $lab 'load-error.log')
    $samples=New-Object 'Collections.Generic.List[object]'
    $sampleNumber=0;$availableMiB=$null
    while(-not $load.HasExited){
        $api.Process.Refresh();$mysqlOwner.Refresh()
        if($sampleNumber++ % 10 -eq 0){$availableMiB=[math]::Round((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory/1024,2)}
        $samples.Add([pscustomobject]@{at=[DateTime]::UtcNow.ToString('o');hostAvailableMiB=$availableMiB;apiCpuSeconds=$api.Process.TotalProcessorTime.TotalSeconds;apiWorkingMiB=[math]::Round($api.Process.WorkingSet64/1MB,2);apiPrivateMiB=[math]::Round($api.Process.PrivateMemorySize64/1MB,2);apiThreads=$api.Process.Threads.Count;apiHandles=$api.Process.HandleCount;mysqlCpuSeconds=$mysqlOwner.TotalProcessorTime.TotalSeconds;mysqlWorkingMiB=[math]::Round($mysqlOwner.WorkingSet64/1MB,2);mysqlPrivateMiB=[math]::Round($mysqlOwner.PrivateMemorySize64/1MB,2)})
        Start-Sleep -Seconds 1
    }
    $samples | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $lab 'resources.csv')
    if($load.ExitCode -ne 0){throw 'Workload or integrity gate failed; inspect result and private diagnostics.'}
    "PASS: synthetic load and integrity. Evidence: $lab"
}finally{
    if($load -and -not $load.HasExited){$load.Kill();$load.WaitForExit()}
    if($api){if(-not $api.Process.HasExited){$api.Process.Kill();$api.Process.WaitForExit()};[IO.File]::WriteAllText((Join-Path $lab 'api.log'),$api.Stdout.Result+$api.Stderr.Result)}
    if($mysqlProcess -and -not $mysqlProcess.HasExited){try{Invoke-Native (Join-Path $bin 'mysqladmin.exe') @("--defaults-extra-file=$cnf",'shutdown') | Out-Null}catch{Stop-Process -Id $mysqlProcess.Id -ErrorAction SilentlyContinue};[void]$mysqlProcess.WaitForExit(30000)}
    $mutex.ReleaseMutex();$mutex.Dispose()
}
