# End-user download directly from Microsoft; no Microsoft binary is redistributed in our EXE.
function Assert-VisualCppRuntime([string]$File) {
    $item=(Get-Content (Join-Path $PSScriptRoot 'prerequisites.json') -Raw | ConvertFrom-Json).items | Where-Object id -eq 'vcredist'
    if((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash -ne $item.sha256){throw 'Visual C++ package does not match the approved Microsoft file.'}
    $signature=Get-AuthenticodeSignature -LiteralPath $File
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Microsoft Corporation(?:,|$)'){throw 'Visual C++ package lacks a valid Microsoft signature.'}
}

function Get-VisualCppRuntime([string]$Prerequisites,[string]$Beside,[string]$Cache) {
    foreach($directory in @($Prerequisites,$Beside) | Where-Object {$_}){
        $file=Join-Path $directory 'vc_redist.x64.exe'
        if(Test-Path -LiteralPath $file){Assert-VisualCppRuntime $file;return $file}
    }
    New-Item -ItemType Directory -Force $Cache | Out-Null
    $file=Join-Path $Cache 'vc_redist.x64.exe'
    if(Test-Path -LiteralPath $file){Assert-VisualCppRuntime $file;return $file}
    $item=(Get-Content (Join-Path $PSScriptRoot 'prerequisites.json') -Raw | ConvertFrom-Json).items | Where-Object id -eq 'vcredist'
    $uri=[uri]$item.url
    if($uri.Scheme -ne 'https' -or $uri.Host -ne 'download.visualstudio.microsoft.com'){throw 'Only the approved Microsoft HTTPS download is permitted.'}
    [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
    'Downloading required Visual C++ runtime directly from Microsoft; verifying hash and publisher.' | Write-Host
    try {
        Invoke-WebRequest -Uri $uri -OutFile ($file+'.partial') -UseBasicParsing -TimeoutSec 180
        Assert-VisualCppRuntime ($file+'.partial')
        Move-Item -LiteralPath ($file+'.partial') -Destination $file
    } catch {
        Remove-Item -LiteralPath ($file+'.partial') -Force -ErrorAction SilentlyContinue
        throw 'The required Microsoft runtime could not be downloaded or verified. No database change was started. Connect to the internet or supply the approved official runtime beside the installer.'
    }
    return $file
}

function Ensure-VisualCppRuntime([string]$MySqlExecutable,[string]$Cache,[string]$Prerequisites,[string]$Beside) {
    try{Invoke-Native $MySqlExecutable @('--version') | Out-Null;return}catch{}
    $runtime=Get-VisualCppRuntime $Prerequisites $Beside $Cache
    $process=Start-Process -FilePath $runtime -ArgumentList @('/install','/quiet','/norestart') -WindowStyle Hidden -PassThru
    $null=$process.Handle
    if(-not $process.WaitForExit(600000) -or $process.ExitCode -notin @(0,1638,3010)){throw 'Microsoft runtime installation failed before database changes.'}
    try{Invoke-Native $MySqlExecutable @('--version') | Out-Null}catch{throw 'Microsoft runtime is not usable yet; restart Windows and rerun the installer. The database was not changed.'}
}
