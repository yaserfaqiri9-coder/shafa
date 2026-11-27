# Compatibility entry point; all arguments are validated by the isolated runner.
& (Join-Path $PSScriptRoot 'run-isolated.ps1') @args
if(-not $?) { exit 1 }
