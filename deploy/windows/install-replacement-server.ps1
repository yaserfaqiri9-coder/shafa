# Full replacement installer entry. The original installation capsule is encrypted
# separately from the executable, and no recovery password is embedded in it.
[CmdletBinding()]
param([string]$Root='C:\AlShifa',[Parameter(Mandatory)][string]$Package,[string]$InstallerFolder,[switch]$Apply)
. (Join-Path $PSScriptRoot 'common.ps1')
$Package=[IO.Path]::GetFullPath($Package).TrimEnd('\')
$password=$env:ALSHIFA_RECOVERY_PASSWORD
Remove-Item Env:\ALSHIFA_RECOVERY_PASSWORD -ErrorAction SilentlyContinue
if(-not $password){throw 'Enter the private installation recovery key.'}
$keyFile=Join-Path $Package 'recovery-input-private.key'
$zip=Join-Path $Package 'original-installation-private.zip'
$source=Join-Path $Package 'original-installation'
if(Test-Path -LiteralPath $source){throw 'Use a new private extraction directory for this installation.'}
try{
    [IO.File]::WriteAllText($keyFile,$password,(New-Object Text.UTF8Encoding($false)));$password=$null
    $gpg=$null;try{$gpg=Find-Gpg}catch{}
    if(-not $gpg){
        $item=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'prerequisites.json') -Raw|ConvertFrom-Json).items|Where-Object id -eq 'gpg4win'
        $installer=Join-Path $Package "prerequisites\$($item.file)"
        if((Get-FileHash -LiteralPath $installer).Hash -cne $item.sha256){throw 'Offline encryption prerequisite integrity check failed.'}
        $signature=Get-AuthenticodeSignature -LiteralPath $installer
        if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'g10 Code GmbH'){throw 'Offline encryption prerequisite publisher check failed.'}
        $process=Start-Process -FilePath $installer -ArgumentList '/S' -WindowStyle Hidden -PassThru
        $null=$process.Handle
        if(-not $process.WaitForExit(600000) -or $process.ExitCode -ne 0){throw 'Offline encryption prerequisite installation failed.'}
        $gpg=Find-Gpg
    }
    '##STEP باز کردن بستهٔ کامل نصب'
    Unprotect-BackupFile $gpg (Join-Path $Package 'original-installation.zip.gpg') $zip $keyFile $Package
    [IO.Compression.ZipFile]::ExtractToDirectory($zip,$source)
    & (Join-Path $PSScriptRoot 'recover-copied-server.ps1') -Source $source -Root $Root -Package $Package -InstallerFolder $InstallerFolder -Apply:$Apply
}catch{
    throw 'Installation stopped. Check the private installation key and retain the original recovery file. Existing hospital information was not replaced.'
}finally{
    foreach($path in @($keyFile,$zip)){if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue}}
    $resolved=[IO.Path]::GetFullPath($source).TrimEnd('\')
    if([IO.Path]::GetDirectoryName($resolved) -ne $Package -or [IO.Path]::GetFileName($resolved) -ne 'original-installation'){throw 'Unexpected temporary source cleanup path.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
