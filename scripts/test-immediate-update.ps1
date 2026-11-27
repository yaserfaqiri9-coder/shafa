# Current updater source with synthetic packages/keys and mocked network/host adapters.
# Never executes an update, contacts a server, registers a task or uses a database.
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\windows\common.ps1')
$work=Join-Path $repo ('.tools\immediate-update-tests-'+[guid]::NewGuid().ToString('N'))
$scripts=Join-Path $work 'scripts';$fixture=Join-Path $work 'fixture'
New-Item -ItemType Directory -Force $scripts,$fixture|Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'deploy\windows\auto-update.ps1'),(Join-Path $repo 'deploy\windows\auto-update-common.ps1') -Destination $scripts
$rsa=New-Object Security.Cryptography.RSACryptoServiceProvider(3072)
try {
 $release=Join-Path $fixture 'release'
 New-Item -ItemType Directory -Force (Join-Path $release 'app'),(Join-Path $release 'scripts')|Out-Null
 [IO.File]::WriteAllText((Join-Path $release 'app\AlShifa.Api.exe'),'synthetic never executed')
 [IO.File]::WriteAllText((Join-Path $release 'app\release.json'),'{"version":"2026.11.27.1","schema":"synthetic-schema"}')
 [IO.File]::WriteAllText((Join-Path $release 'scripts\update.ps1'),'# synthetic never executed')
 $package=Join-Path $fixture 'package.zip';[IO.Compression.ZipFile]::CreateFromDirectory($release,$package)
 $manifest=@{version='2026.11.27.1';schema='synthetic-schema';packageUrl='https://publisher.example/package.zip';sha256=(Get-FileHash $package).Hash;sizeBytes=(Get-Item $package).Length}
 $bytes=[Text.Encoding]::UTF8.GetBytes(($manifest|ConvertTo-Json));[IO.File]::WriteAllBytes((Join-Path $fixture 'manifest.json'),$bytes)
 [IO.File]::WriteAllText((Join-Path $fixture 'manifest.sig'),[Convert]::ToBase64String($rsa.SignData($bytes,'SHA256')))
 [IO.File]::WriteAllText((Join-Path $scripts 'common.ps1'),@'
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
function Invoke-Native($Exe,$Arguments) {
 if($Exe -ne 'powershell.exe' -or $Arguments -notcontains '-BackupDestination'){throw 'Unexpected synthetic native operation'}
 [IO.File]::WriteAllText((Join-Path $Root 'installation-requested.txt'),'synthetic update adapter only')
}
function Get-WinEvent {
 $path=Join-Path $Root 'synthetic-event.json'
 if(-not(Test-Path $path)){throw 'No event'}
 $event=Get-Content $path -Raw|ConvertFrom-Json
 [pscustomobject]@{TimeCreated=[datetime]::Parse($event.time);Message=$event.message}
}
'@)
 Add-Content -LiteralPath (Join-Path $scripts 'auto-update-common.ps1') -Value @'
function Receive-UpdateFile($Uri,$Path,$MaximumBytes) {
 $fixture=Join-Path (Split-Path $PSScriptRoot -Parent) 'fixture'
 $name=if($Uri.AbsolutePath.EndsWith('.sig')){'manifest.sig'}elseif($Uri.AbsolutePath.EndsWith('.json')){'manifest.json'}else{'package.zip'}
 Copy-Item -LiteralPath (Join-Path $fixture $name) -Destination $Path
}
'@
 foreach($case in 'legacy-auto-true','sync-held','approved-next-poll','approval-without-event','wrong-version','tampered-manifest') {
  $root=Join-Path $work $case
  foreach($dir in 'app','keys','logs','secrets','requests'){New-Item -ItemType Directory -Force (Join-Path $root $dir)|Out-Null}
  $cfg=@{Sync=@{Url='https://online.example';Key='synthetic-preserve';IntervalMinutes=5;RequireCompatibilityApproval=($case -eq 'sync-held')}}
  $cfg|ConvertTo-Json -Depth 8|Set-Content (Join-Path $root 'app\appsettings.Production.json')
  $configHash=(Get-FileHash (Join-Path $root 'app\appsettings.Production.json')).Hash
  [IO.File]::WriteAllText((Join-Path $root 'app\release.json'),'{"version":"2026.11.26.1"}')
  [IO.File]::WriteAllText((Join-Path $root 'secrets\update-public.xml'),$rsa.ToXmlString($false))
  @{manifestUrl='https://publisher.example/manifest.json';autoInstall=$true;backupDestination='synthetic'}|ConvertTo-Json|Set-Content (Join-Path $root 'secrets\auto-update.json')
  if($case -in @('approved-next-poll','approval-without-event','wrong-version')) {
   @{version=$(if($case -eq 'wrong-version'){'2026.11.28.1'}else{$manifest.version});approvedAt=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json|Set-Content (Join-Path $root 'requests\update-approval.json')
   if($case -ne 'approval-without-event'){@{time=[DateTime]::UtcNow.ToString('o');message="AlShifa update approval $($manifest.version)"}|ConvertTo-Json|Set-Content (Join-Path $root 'synthetic-event.json')}
  }
  if($case -eq 'tampered-manifest'){[IO.File]::WriteAllText((Join-Path $fixture 'manifest.json'),'{}')}
  $previousPreference=$ErrorActionPreference
  try {
   $ErrorActionPreference='Continue'
   & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'auto-update.ps1') -Root $root 2>&1|Out-Null
   $code=$LASTEXITCODE
  } finally {$ErrorActionPreference=$previousPreference}
  $state=Get-Content (Join-Path $root 'logs\update-status.json') -Raw|ConvertFrom-Json
  $installed=Test-Path (Join-Path $root 'installation-requested.txt')
  if($installed -ne ($case -eq 'approved-next-poll')){throw "Unauthorized or missing install: $case"}
  if($state.state -ne $(if($installed){'installed'}elseif($case -in @('tampered-manifest','approval-without-event')){'failed'}else{'awaitingApproval'})){throw "Incorrect state: $case"}
  if(($code -ne 0) -ne ($case -in @('tampered-manifest','approval-without-event'))){throw "Incorrect exit: $case"}
  if((Get-FileHash (Join-Path $root 'app\appsettings.Production.json')).Hash -cne $configHash){throw 'Updater changed Sync configuration'}
  "PASS: $case; no unapproved install, Sync unchanged."
 }
 # Actual task constructors, intercepted registration only.
 Import-Module ScheduledTasks
 function Protect-Path {}
 function Get-ScheduledTask {}
 function Register-ScheduledTask($TaskName,$Action,$Trigger,$Settings,$User,$RunLevel,[switch]$Force){
  if($TaskName -eq 'AlShifa Update Check'){$script:checkTask=@{Triggers=$Trigger;User=$User}}
 }
 if((Get-Command Register-ScheduledTask).CommandType -ne 'Function'){throw 'Task registration mock is not active'}
 Set-AutoUpdateTasks (Join-Path $work 'task-host')
 if($checkTask.User -ne 'SYSTEM' -or $checkTask.Triggers.Count -ne 2 -or @($checkTask.Triggers|Where-Object {$_.Repetition.Interval -eq 'PT1M'}).Count -ne 1){throw 'Minute/startup schedule missing'}
 'PASS: startup and every-minute update detection, no real task registered.'
 . (Join-Path $repo 'deploy\windows\auto-update-common.ps1')
 $enrolled=Join-Path $work 'enrolled';New-Item -ItemType Directory -Force (Join-Path $enrolled 'secrets')|Out-Null
 @{manifestUrl='https://original.example/feed.json';backupDestination='Z:\Keep';allowSameDisk=$false;autoInstall=$true}|ConvertTo-Json|Set-Content (Join-Path $enrolled 'secrets\auto-update.json')
 Enable-PublisherFeed $enrolled (Join-Path $repo 'deploy\windows') 'X:\Other' $true|Out-Null
 $settings=Get-Content (Join-Path $enrolled 'secrets\auto-update.json') -Raw|ConvertFrom-Json
 if($settings.autoInstall -ne $false -or $settings.manifestUrl -ne 'https://original.example/feed.json' -or $settings.backupDestination -ne 'Z:\Keep' -or $settings.allowSameDisk -ne $false){throw 'Enrollment changed existing destinations or enabled unattended install'}
 'PASS: old night policy becomes approval-only; publisher and backup settings preserved.'
} finally {$rsa.Dispose()}
"Synthetic evidence: $work"
