# Browser smoke test on an already-created private automation lab, with a new synthetic database.
param([Parameter(Mandatory)][string]$Lab, [Parameter(Mandatory)][string]$BrowserScript)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')
$labMutex = New-Object System.Threading.Mutex($false, 'Local\AlShifa.IsolatedVerification')
try { $ownsLab = $labMutex.WaitOne(0) } catch [System.Threading.AbandonedMutexException] { $ownsLab = $true }
if (-not $ownsLab) { $labMutex.Dispose(); throw 'Another isolated browser/database lab owns the verification lock.' }
$Lab = [IO.Path]::GetFullPath($Lab).TrimEnd('\')
if ((Split-Path $Lab -Parent) -ne (Join-Path $repo '.tools') -or (Split-Path $Lab -Leaf) -notmatch '^automation-lab-[a-f0-9]{32}$') { throw 'Select only a private lab created by test-automation-isolated.ps1.' }
if (Get-Process testhost -ErrorAction SilentlyContinue) { throw 'Wait for database tests to finish.' }
$cnf = Join-Path $Lab 'root.cnf'
$ini = Join-Path $Lab 'mysql.ini'
$bin = Join-Path $repo '.tools\mysql-8.4.11-winx64\bin'
$password = ((Get-Content -LiteralPath $cnf | Where-Object { $_ -match '^password=' }) -replace '^password=', '')
$port = [int](((Get-Content -LiteralPath $cnf | Where-Object { $_ -match '^port=' }) -replace '^port=', ''))
if ($port -lt 33379 -or $port -gt 33399 -or -not (Test-PortFree $port)) { throw 'Lab port is invalid or occupied.' }
$apiPort = Select-FreePort @(33400..33410) 'isolated browser API'
$uiPort = Select-FreePort @(33420..33430) 'isolated browser UI'
$variables = @('ConnectionStrings__Default','ASPNETCORE_ENVIRONMENT','ASPNETCORE_URLS','ALSHIFA_ADMIN_USER','ALSHIFA_ADMIN_PASSWORD','ALSHIFA_API','ALSHIFA_E2E_BASE','ALSHIFA_E2E_USER','ALSHIFA_E2E_PASSWORD','ALSHIFA_E2E_ARTIFACTS','Mirror__Enabled','Sync__Url','DOTNET_ROOT')
$saved = @{}; foreach ($name in $variables) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$mysqlProcess = $null; $apiProcess = $null; $uiProcess = $null
try {
    $mysqlProcess = Start-Process -FilePath (Join-Path $bin 'mysqld.exe') -ArgumentList "--defaults-file=`"$ini`"" -WindowStyle Hidden -PassThru
    $verified = $false
    for ($attempt=0; $attempt -lt 60; $attempt++) {
        try {
            $row = @(Invoke-Native (Join-Path $bin 'mysql.exe') @("--defaults-extra-file=$cnf", '-N', '-B', '--raw', '-e', 'SELECT @@port, @@datadir;'))[-1] -split "`t"
            if ([int]$row[0] -ne $port -or [IO.Path]::GetFullPath($row[1]).TrimEnd('\') -ne (Join-Path $Lab 'data')) { throw 'Lab identity mismatch.' }
            $listener = Get-NetTCPConnection -State Listen -LocalPort $port -ErrorAction Stop
            $owner = Get-CimInstance Win32_Process -Filter "ProcessId=$($listener.OwningProcess)"
            if ($listener.LocalAddress -ne '127.0.0.1' -or -not $owner.CommandLine.Contains($ini)) { throw 'Lab process mismatch.' }
            $verified=$true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $verified) { throw 'Private lab MySQL was not verified.' }
    'Verified private lab server; creating only alshifa_browser_test with synthetic credentials.'
    $env:ConnectionStrings__Default = "Server=127.0.0.1;Port=$port;Database=alshifa_browser_test;User=root;Password=$password;"
    $env:ASPNETCORE_ENVIRONMENT = 'Testing'; $env:Mirror__Enabled='false'; $env:Sync__Url=''
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$apiPort"
    $env:ALSHIFA_ADMIN_USER = ('automation_' + [Guid]::NewGuid().ToString('N').Substring(0,12)); $env:ALSHIFA_ADMIN_PASSWORD = 'Lab-' + (New-Secret) + '-7a'
    $env:ALSHIFA_E2E_USER = $env:ALSHIFA_ADMIN_USER; $env:ALSHIFA_E2E_PASSWORD = $env:ALSHIFA_ADMIN_PASSWORD
    $env:ALSHIFA_E2E_BASE = "http://127.0.0.1:$uiPort"; $env:ALSHIFA_API=$env:ASPNETCORE_URLS
    $env:ALSHIFA_E2E_ARTIFACTS=Join-Path $Lab 'browser'
    $env:DOTNET_ROOT = Join-Path $repo '.tools\dotnet'
    New-Item -ItemType Directory -Force $env:ALSHIFA_E2E_ARTIFACTS | Out-Null
    $app = Join-Path $repo '.tools\automation-build\bin\AlShifa.Api\debug\AlShifa.Api.dll'
    $dotnet = Join-Path $env:DOTNET_ROOT 'dotnet.exe'
    Invoke-Native $dotnet @($app, '--create-admin', '--contentRoot', $Lab) | Out-Null
    $apiProcess = Start-Process -FilePath $dotnet -ArgumentList @("`"$app`"", '--contentRoot', "`"$Lab`"") -WorkingDirectory $Lab -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $Lab 'api.stdout') -RedirectStandardError (Join-Path $Lab 'api.stderr')
    $node = (Get-Command node.exe).Source
    $vite = Join-Path $repo 'frontend\node_modules\vite\bin\vite.js'
    $uiProcess = Start-Process -FilePath $node -ArgumentList @("`"$vite`"", '--host', '127.0.0.1', '--port', "$uiPort", '--strictPort') -WorkingDirectory (Join-Path $repo 'frontend') -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $Lab 'vite.stdout') -RedirectStandardError (Join-Path $Lab 'vite.stderr')
    $ready=$false
    for ($attempt=0; $attempt -lt 60; $attempt++) {
        try {
            if ((Invoke-WebRequest ($env:ASPNETCORE_URLS + '/health') -UseBasicParsing).Content -ne 'Healthy') { throw 'API not healthy.' }
            Invoke-WebRequest ($env:ALSHIFA_E2E_BASE + '/login') -UseBasicParsing | Out-Null
            $ready=$true; break
        } catch { Start-Sleep -Milliseconds 500 }
    }
    if (-not $ready) { throw 'Isolated browser servers were not ready.' }
    & $node 'C:\Users\PARSA COMPUTER\.agents\skills\playwright\run.js' $BrowserScript
    if ($LASTEXITCODE -ne 0) { throw 'Browser smoke test failed.' }
} finally {
    foreach ($process in @($uiProcess,$apiProcess)) { if ($process -and -not $process.HasExited) { Stop-Process -Id $process.Id } }
    if ($mysqlProcess) { try { Invoke-Native (Join-Path $bin 'mysqladmin.exe') @("--defaults-extra-file=$cnf", 'shutdown') | Out-Null } catch { } }
    foreach ($name in $variables) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
    'Isolated browser processes stopped; no installed service was changed.'
    $labMutex.ReleaseMutex(); $labMutex.Dispose()
}
