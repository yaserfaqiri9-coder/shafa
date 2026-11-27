$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'deploy\verify-voice-assets.ps1')
$original=Assert-RecordedVoiceAssets $repo
$scratch=Join-Path $repo ('.tools\voice-release-test-'+[guid]::NewGuid().ToString('N'))
try {
    $code=Join-Path $scratch 'backend\src\AlShifa.Api\Features\Calls'
    New-Item -ItemType Directory -Force $code | Out-Null
    Copy-Item (Join-Path $repo 'backend\src\AlShifa.Api\Features\Calls\PatientCall.cs') $code
    $refused=$false
    try { Assert-RecordedVoiceAssets $scratch | Out-Null } catch { $refused=$true }
    if(-not $refused){throw 'A source snapshot without recorded voice assets was accepted.'}
    $destination=Join-Path $scratch 'frontend\public\audio\recorded-queue-20261120\WAV'
    New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath $original -Destination $destination -Recurse
    Assert-RecordedVoiceAssets $scratch | Out-Null
    $backup=Join-Path (Split-Path $destination -Parent) 'WAV_backup_20261120'
    New-Item -ItemType Directory $backup | Out-Null
    [IO.File]::WriteAllText((Join-Path $backup 'obsolete.txt'),'synthetic duplicate')
    $public=Join-Path $scratch 'published'
    Copy-ReleaseWebFiles (Join-Path $scratch 'frontend\public') $public
    if(Test-Path (Join-Path $public 'audio\recorded-queue-20261120\WAV_backup_20261120')){throw 'Obsolete voice backup was shipped.'}
    if(-not(Test-Path (Join-Path $public 'audio\recorded-queue-20261120\WAV\numbers\number_001.mp3'))){throw 'Required voice recording was omitted.'}
    Remove-Item -LiteralPath (Join-Path $destination 'numbers\number_001.mp3')
    $refused=$false
    try { Assert-RecordedVoiceAssets $scratch | Out-Null } catch { $refused=$true }
    if(-not $refused){throw 'A voice pack missing one required number was accepted.'}
    'PASS: release guard accepts the established pack and refuses absent/incomplete packs.'
} finally {
    $resolved=[IO.Path]::GetFullPath($scratch)
    if(-not $resolved.StartsWith((Join-Path $repo '.tools\voice-release-test-'),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe cleanup path.'}
    if(Test-Path -LiteralPath $resolved){Remove-Item -LiteralPath $resolved -Recurse -Force}
}
