# Recorded queue announcements are ignored by Git but required at runtime.
# Validate before either build, so a source snapshot cannot silently publish a mute hospital.
function Assert-RecordedVoiceAssets([string]$RepositoryRoot) {
    $source=[IO.File]::ReadAllText((Join-Path $RepositoryRoot 'backend\src\AlShifa.Api\Features\Calls\PatientCall.cs'))
    $version=[regex]::Match($source,'RecordedVersion\s*=\s*"(recorded-[a-z0-9-]+)"').Groups[1].Value
    $maximum=[regex]::Match($source,'RecordedMaxNumber\s*=\s*(\d+)').Groups[1].Value
    $destinations=[regex]::Match($source,'RecordedDestinations\s*=\s*\[([^\]]+)\]').Groups[1].Value
    if(-not $version -or -not $maximum -or -not $destinations){throw 'Recorded voice metadata could not be verified.'}
    $root=Join-Path $RepositoryRoot "frontend\public\audio\$version\WAV"
    $required=@(1..[int]$maximum | ForEach-Object {'numbers\number_{0:D3}.mp3' -f $_})
    $required+=@([regex]::Matches($destinations,'"([a-z_]+)"') | ForEach-Object {'destinations\destination_'+$_.Groups[1].Value+'.wav'})
    foreach($relative in $required){
        $file=Join-Path $root $relative
        if(-not(Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -lt 16){throw "Required recorded voice asset missing or empty: $relative. Restore the established voice pack before building."}
    }
    return $root
}

# Preserve the developer's recordings/backups while excluding obsolete backup copies from shipped public files.
function Copy-ReleaseWebFiles([string]$Source,[string]$Destination) {
    $sourceRoot=(Resolve-Path -LiteralPath $Source).Path.TrimEnd('\','/')
    New-Item -ItemType Directory -Force $Destination | Out-Null
    foreach($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File){
        $relative=$file.FullName.Substring($sourceRoot.Length).TrimStart('\','/')
        if($relative -match '^audio[\\/]recorded-[^\\/]+[\\/]WAV_backup_[^\\/]+[\\/]'){continue}
        $target=Join-Path $Destination $relative
        New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $target
    }
}
