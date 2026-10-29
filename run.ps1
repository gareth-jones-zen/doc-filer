<#
.SYNOPSIS
    Converts documents in a staging folder to markdown and files them into a repo.

.EXAMPLE
    ./run.ps1 --config C:\path\to\doc-filer.json
    ./run.ps1 --config C:\path\to\doc-filer.json --dry-run
    ./run.ps1 --config C:\path\to\doc-filer.json --watch --log C:\path\to\doc-filer.log
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$ErrorActionPreference = 'Stop'

$project = Join-Path $PSScriptRoot 'src\DocFiler.csproj'

if ($Arguments -contains '--watch') {
    # Watch mode runs a published copy, not the build output, so `dotnet run` can still
    # rebuild for one-off runs while the watcher is running (it would otherwise find the
    # build output locked). If publishing fails because the copy is already running,
    # the existing copy is used.
    $published = Join-Path $env:LOCALAPPDATA 'doc-filer\watch'
    dotnet publish $project -c Release -o $published --nologo -v quiet | Out-Null
    & (Join-Path $published 'doc-filer.exe') @Arguments
    exit $LASTEXITCODE
}

dotnet run --project $project -- @Arguments

exit $LASTEXITCODE
