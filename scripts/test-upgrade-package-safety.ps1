# Static/cryptographic checks only; never starts a service or changes a database.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')
. (Join-Path $repo 'deploy\windows\runtime-common.ps1')
. (Join-Path $repo 'deploy\windows\upgrade-safety.ps1')
function Refused([scriptblock]$Action,[string]$Name){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};if(-not $failed){throw "Unsafe action accepted: $Name"};"PASS: $Name refused"}
$work=Join-Path $repo ('.tools\upgrade-package-safety-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
Protect-Path $work @("$([Security.Principal.WindowsIdentity]::GetCurrent().Name)=F")
$fake=Join-Path $work 'vc_redist.x64.exe';[IO.File]::WriteAllText($fake,'synthetic tampered runtime, never executed')
Refused {Assert-VisualCppRuntime $fake} 'tampered or unsigned Visual C++ package'
Refused {Get-UpgradeDataProof 'never-executed' '' 'alshifa'} 'live database as an implicit rehearsal target'
Refused {& (Join-Path $repo 'deploy\windows\restore-test.ps1') -Backup 'never-opened' -Root $work -TargetDatabase alshifa} 'restore to a live database name'
$cache=Get-ChildItem (Join-Path $repo '.tools') -Directory -Filter 'runtime-download-test-*' | Where-Object {Test-Path (Join-Path $_.FullName 'vc_redist.x64.exe')} | Select-Object -First 1
if(-not $cache){throw 'Run the direct Microsoft download check before this test.'}
Assert-VisualCppRuntime (Join-Path $cache.FullName 'vc_redist.x64.exe')
'PASS: directly downloaded official runtime has approved SHA-256 and valid Microsoft signature'
$assembly=[Reflection.Assembly]::LoadFile((Join-Path $repo '.tools\upgrade-installer-stub.exe'))
$hostType=$assembly.GetType('AlShifa.Setup.Host')
$method=$hostType.GetMethod('RootFromServicePath',[Reflection.BindingFlags]'Static,NonPublic')
foreach($path in 'C:\AlShifa\app\AlShifa.Api.exe','"D:\Hospital System\app\AlShifa.Api.exe"'){
    if(-not $method.Invoke($null,@($path))){throw 'A valid installed service path was rejected.'}
}
foreach($path in 'relative\app\AlShifa.Api.exe','C:\unrelated.exe','C:\AlShifa\app\AlShifa.Api.exe --other'){
    if($method.Invoke($null,@($path))){throw 'An ambiguous service path was accepted.'}
}
'PASS: EXE derives roots from valid installed service paths and refuses unrelated/ambiguous paths'
