#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$PackageDirectory,[Parameter(Mandatory)][string]$ReturnDirectory)
$ErrorActionPreference='Stop'
try {
    Add-Type -Path (Join-Path $PackageDirectory 'ReviewCrypto.cs') -ReferencedAssemblies System.Core,System.Security,System.Web.Extensions
    . (Join-Path $PackageDirectory 'Review.Core.ps1')
    $script:reviewExit=3
    . (Join-Path $PackageDirectory 'Session.Agent.ps1')
    if(-not(Test-Path -LiteralPath (Join-Path $PackageDirectory 'relay.json'))){throw 'Live session configuration required'}
    Invoke-HospitalSession $PackageDirectory|ForEach-Object {
        if($_ -is [string] -and $_ -match '^(STAGE|RESULT):'){Write-Output $_}
        elseif($_ -is [int]){$script:reviewExit=$_}
    }
    exit $script:reviewExit
} catch {
    Write-Output 'RESULT:HELD:package_execution_failed'
    exit 3
}
