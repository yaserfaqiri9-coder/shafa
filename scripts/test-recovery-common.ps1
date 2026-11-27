# Synthetic fixtures only. No SQL, services, tasks, certificates or network changes.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')
. (Join-Path $repo 'deploy\windows\recovery-common.ps1')
$work=Join-Path $repo ('.tools\recovery-guards-'+[guid]::NewGuid().ToString('N'))
$source=Join-Path $work 'source';$target=Join-Path $work 'target'
New-Item -ItemType Directory -Force $source|Out-Null
foreach($folder in 'app','mysql\bin','keys','secrets','certs','storage'){New-Item -ItemType Directory -Force (Join-Path $source $folder)|Out-Null}
foreach($path in 'app\AlShifa.Api.exe','app\AlShifa.Api.dll','mysql\bin\mysqld.exe','keys\sync-installation-id','secrets\backup-password.txt','secrets\mysql-root.txt','secrets\backup.cnf','secrets\restore.cnf','certs\server.pfx'){
    [IO.File]::WriteAllText((Join-Path $source $path),'synthetic-never-executed')
}
$config=@{ConnectionStrings=@{Default='Server=127.0.0.1;Port=33494;Database=alshifa;User=synthetic;Password=synthetic'};Mirror=@{Enabled=$false}}
$configFile=Join-Path $source 'app\appsettings.Production.json'
function Set-Config {$config|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $configFile -Encoding UTF8}
function Manifest {
    @{sourceFiles=@(foreach($path in 'app\AlShifa.Api.dll','app\appsettings.Production.json','mysql\bin\mysqld.exe','keys\sync-installation-id'){@{path=$path;sha256=(Get-FileHash -LiteralPath (Join-Path $source $path)).Hash}})}
}
function Reject([scriptblock]$Action,[string]$Name){$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};if(-not $failed){throw "Unsafe fixture accepted: $Name"}}
Set-Config
$manifest=Manifest
Assert-RecoverySource $source $manifest|Out-Null
[IO.File]::WriteAllText((Join-Path $source 'app\AlShifa.Api.dll'),'changed-synthetic')
Reject {Assert-RecoverySource $source $manifest} 'changed application'
$manifest=Manifest
$wrong=@{sourceFiles=@($manifest.sourceFiles[0],$manifest.sourceFiles[0],$manifest.sourceFiles[2],$manifest.sourceFiles[3])}
Reject {Assert-RecoverySource $source $wrong} 'duplicate manifest mapping'
$config.ConnectionStrings.Default='Server=127.0.0.1;Port=3308;Database=alshifa;User=synthetic;Password=synthetic';Set-Config;$manifest=Manifest
Reject {Assert-RecoverySource $source $manifest} 'installed hospital port'
$config.ConnectionStrings.Default='Server=remote.invalid;Port=33494;Database=alshifa;User=synthetic;Password=synthetic';Set-Config;$manifest=Manifest
Reject {Assert-RecoverySource $source $manifest} 'remote database'
$config.ConnectionStrings.Default='Server=127.0.0.1;Port=33494;Database=alshifa;User=synthetic;Password=synthetic';Set-Config
foreach($path in 'mysql\bin\runtime.dll','mysql\bin\debug.pdb','mysql\lib\static.lib','mysql\lib\plugin\plugin.dll','app\keep.pdb','storage\synthetic.txt'){
    New-Item -ItemType Directory -Force (Split-Path (Join-Path $source $path) -Parent)|Out-Null
    [IO.File]::WriteAllText((Join-Path $source $path),'synthetic')
}
$before=@(Get-ChildItem -LiteralPath $source -File -Recurse|ForEach-Object {$_.FullName+'|'+(Get-FileHash -LiteralPath $_.FullName).Hash}|Sort-Object)
Copy-RecoveryRuntime $source $target
foreach($path in 'mysql\bin\runtime.dll','mysql\lib\plugin\plugin.dll','app\keep.pdb','keys\sync-installation-id','storage\synthetic.txt'){
    if(-not(Test-Path -LiteralPath (Join-Path $target $path))){throw 'A required runtime or hospital file was omitted'}
}
foreach($path in 'mysql\bin\debug.pdb','mysql\lib\static.lib'){if(Test-Path -LiteralPath (Join-Path $target $path)){throw 'Non-runtime development files were copied'}}
$after=@(Get-ChildItem -LiteralPath $source -File -Recurse|ForEach-Object {$_.FullName+'|'+(Get-FileHash -LiteralPath $_.FullName).Hash}|Sort-Object)
if(@(Compare-Object $before $after).Count){throw 'The source fixture was changed'}
'PASS: source integrity, duplicate mappings, remote/3308 refusal, runtime filtering and original-folder preservation.'
