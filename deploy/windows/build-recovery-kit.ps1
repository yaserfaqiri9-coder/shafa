# Dedicated replacement recovery, not an application upgrade. -Full includes the
# selected hospital's runtime and original identity in a password-encrypted capsule.
param([Parameter(Mandatory)][string]$Source,[Parameter(Mandatory)][string]$VerifiedSql,[Parameter(Mandatory)][string]$VerifiedProof,[Parameter(Mandatory)][string]$Verification,[Parameter(Mandatory)][string]$Output,[switch]$Full,[switch]$IncludeVisualCppRuntime)
. (Join-Path $PSScriptRoot 'common.ps1')
. (Join-Path $PSScriptRoot 'recovery-common.ps1')
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$Source=(Resolve-Path -LiteralPath $Source).Path
$check=Get-Content -LiteralPath $Verification -Raw|ConvertFrom-Json
if(-not($check.SourceUntouched -and $check.AllTableContentsMatch -and $check.AllTableChecksPass -and $check.OnlineHistoryCompatible) -or @($check.ChangedTablesComparedToEarlierCandidate|Where-Object {$_ -notin @('auditlogs','syncstates','synccommands')}).Count){throw 'Recovery content or history has not passed verification.'}
if(Test-Path -LiteralPath $Output){throw 'Choose a new output folder; existing deliverables are preserved.'}
$Output=[IO.Path]::GetFullPath($Output)
New-Item -ItemType Directory -Path $Output|Out-Null
$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
Invoke-Native 'icacls.exe' @($Output,'/inheritance:r','/grant:r',"*${sid}:(OI)(CI)F",'*S-1-5-18:(OI)(CI)F','*S-1-5-32-544:(OI)(CI)F')|Out-Null
$package=Join-Path $Output 'package-private';New-Item -ItemType Directory $package|Out-Null
try{
    $scripts=Join-Path $package 'scripts';New-Item -ItemType Directory $scripts|Out-Null
    Copy-Item -Path (Join-Path $PSScriptRoot '*.ps1'),(Join-Path $PSScriptRoot '*.json') -Destination $scripts
    $encrypted=Join-Path $package 'database.sql.gpg'
    Protect-BackupFile (Find-Gpg) $VerifiedSql $encrypted (Join-Path $Source 'secrets\backup-password.txt') $Output
    Copy-Item -LiteralPath $VerifiedProof -Destination (Join-Path $package 'table-proof.txt')
    $sourceFiles=@(foreach($path in 'app\AlShifa.Api.dll','app\appsettings.Production.json','mysql\bin\mysqld.exe','keys\sync-installation-id'){@{path=$path;sha256=(Get-FileHash -LiteralPath (Join-Path $Source $path)).Hash}})
    $manifest=@{format=1;restoreVerified=$true;onlineHistoryVerified=$true;sourceFiles=$sourceFiles;sqlHash=(Get-FileHash -LiteralPath $VerifiedSql).Hash;encryptedHash=(Get-FileHash -LiteralPath $encrypted).Hash;proofHash=(Get-FileHash -LiteralPath (Join-Path $package 'table-proof.txt')).Hash}
    [IO.File]::WriteAllText((Join-Path $package 'recovery-manifest.json'),($manifest|ConvertTo-Json -Depth 8))
    $item=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerequisites.json') -Raw|ConvertFrom-Json).items|Where-Object id -eq 'gpg4win'
    $gpgInstaller=Join-Path $repo ".tools\installer-cache\gpg4win\$($item.file)"
    if((Get-FileHash -LiteralPath $gpgInstaller).Hash -cne $item.sha256){throw 'The approved offline encryption prerequisite is unavailable.'}
    $signature=Get-AuthenticodeSignature -LiteralPath $gpgInstaller
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'g10 Code GmbH'){throw 'Encryption prerequisite publisher check failed.'}
    New-Item -ItemType Directory (Join-Path $package 'prerequisites'),(Join-Path $package 'tools')|Out-Null
    Copy-Item -LiteralPath $gpgInstaller -Destination (Join-Path $package 'prerequisites')
    if($IncludeVisualCppRuntime){
        $vc=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerequisites.json') -Raw|ConvertFrom-Json).items|Where-Object id -eq 'vcredist'
        $file=Join-Path $repo ".tools\installer-cache\vcredist\$($vc.file)"
        if((Get-FileHash -LiteralPath $file).Hash -cne $vc.sha256){throw 'Offline Microsoft prerequisite integrity check failed.'}
        $signed=Get-AuthenticodeSignature -LiteralPath $file
        if($signed.Status -ne 'Valid' -or $signed.SignerCertificate.Subject -notmatch 'Microsoft Corporation'){throw 'Offline Microsoft prerequisite publisher check failed.'}
        Copy-Item -LiteralPath $file -Destination (Join-Path $package 'prerequisites')
    }
    if($Full){
        $staging=Join-Path $Output 'source-staging-private';New-Item -ItemType Directory $staging|Out-Null
        Copy-RecoveryRuntime $Source $staging
        $sourceZip=Join-Path $Output 'source-private.zip'
        [IO.Compression.ZipFile]::CreateFromDirectory($staging,$sourceZip,[IO.Compression.CompressionLevel]::Optimal,$false)
        Protect-BackupFile (Find-Gpg) $sourceZip (Join-Path $package 'original-installation.zip.gpg') (Join-Path $Source 'secrets\backup-password.txt') $Output
        Copy-Item -LiteralPath (Join-Path $Source 'secrets\backup-password.txt') -Destination (Join-Path $Output 'INSTALLATION-KEY-PRIVATE.txt')
    }
    $csc=Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $refs=@('System.Windows.Forms.dll','System.Drawing.dll','System.Core.dll','System.IO.Compression.dll','System.IO.Compression.FileSystem.dll','System.Management.dll','System.ServiceProcess.dll')|ForEach-Object {"/r:$_"}
    $launcher=Join-Path $package 'tools\AlShifa.exe'
    Invoke-Native $csc (@('/nologo','/target:winexe','/optimize+','/codepage:65001',"/out:$launcher")+$refs+@((Join-Path $PSScriptRoot 'installer\Launcher.cs'),(Join-Path $PSScriptRoot 'installer\Discovery.cs')))|Out-Null
    $exe=Join-Path $Output $(if($Full){'Install-AlShifa.exe'}else{'Recover-AlShifa.exe'})
    $defines=@(if($Full){'/define:FULL_RECOVERY'})
    Invoke-Native $csc (@('/nologo','/target:winexe','/optimize+','/codepage:65001',"/win32manifest:$(Join-Path $PSScriptRoot 'installer\app.manifest')","/out:$exe")+$defines+$refs+@((Join-Path $PSScriptRoot 'installer\RecoverCopiedServer.cs')))|Out-Null
    $zip=Join-Path $Output 'payload-private.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($package,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
    $input=[IO.File]::OpenRead($zip);$sha=[Security.Cryptography.SHA256]::Create()
    try{$digest=$sha.ComputeHash($input);$length=$input.Length;$input.Position=0;$stream=[IO.File]::Open($exe,'Append');try{$input.CopyTo($stream);$stream.Write([BitConverter]::GetBytes([long]$length),0,8);$stream.Write($digest,0,32);$mark=[Text.Encoding]::ASCII.GetBytes('ALSHIFA1');$stream.Write($mark,0,8)}finally{$stream.Dispose()}}finally{$input.Dispose();$sha.Dispose()}
    Copy-Item -LiteralPath $Verification -Destination (Join-Path $Output 'verification-redacted.json')
    $notices="The unmodified offline prerequisites are verified using deploy/windows/prerequisites.json.`r`nGpg4win $($item.version): $($item.license)`r`n$($item.licenseNote)`r`n"
    if($IncludeVisualCppRuntime){$notices+="Microsoft Visual C++ $($vc.version): $($vc.license)`r`n$($vc.licenseNote)`r`n"}
    [IO.File]::WriteAllText((Join-Path $Output 'THIRD-PARTY-NOTICES.txt'),$notices)
    [IO.File]::WriteAllText((Join-Path $Output 'SHA256.txt'),((Get-FileHash -LiteralPath $exe).Hash+'  '+(Split-Path $exe -Leaf)))
    "Recovery tool ready: $exe ($([Math]::Round((Get-Item -LiteralPath $exe).Length/1MB,1)) MB)"
}finally{
    # This new, exact build folder contains encrypted data and public tooling only.
    $expected=Join-Path $Output 'payload-private.zip'
    if(Test-Path -LiteralPath $expected){Remove-Item -LiteralPath $expected -Force}
    $resolved=[IO.Path]::GetFullPath($package).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($resolved) -ne $Output -or [IO.Path]::GetFileName($resolved) -ne 'package-private'){throw 'Unexpected build cleanup path.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
    foreach($name in 'source-staging-private','source-private.zip'){
        $child=[IO.Path]::GetFullPath((Join-Path $Output $name))
        if([IO.Path]::GetDirectoryName($child) -ne $Output){throw 'Unexpected source cleanup path.'}
        if(Test-Path -LiteralPath $child){Remove-Item -LiteralPath $child -Recurse -Force}
    }
}
