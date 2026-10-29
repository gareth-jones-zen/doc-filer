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

# pandoc ships with the tool, zipped (pandoc.exe is over GitHub's 100MB file limit), and is
# unpacked here on first use, so nobody has to install it. A config that names its own
# pandoc still wins; this is the default.
$pandocZip = Get-ChildItem (Join-Path $PSScriptRoot 'vendor') -Filter 'pandoc-*-windows-x86_64.zip' |
    Sort-Object Name | Select-Object -Last 1
if ($pandocZip) {
    $pandocDir = Join-Path $PSScriptRoot ('vendor\' + $pandocZip.BaseName)
    $pandocExe = Join-Path $pandocDir 'pandoc.exe'
    if (-not (Test-Path $pandocExe)) {
        # Unpack beside the target and rename, so an interrupted unpack never looks complete.
        $staging = "$pandocDir.unpacking"
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        Expand-Archive -Path $pandocZip.FullName -DestinationPath $staging
        Rename-Item $staging (Split-Path $pandocDir -Leaf)
    }
    $env:DOC_FILER_PANDOC = $pandocExe
}

if ($Arguments -contains '--watch') {
    # Watch mode runs a published copy, not the build output, so `dotnet run` can still
    # rebuild for one-off runs while the watcher is running (it would otherwise find the
    # build output locked). If publishing fails because the copy is already running,
    # the existing copy is used.
    $published = Join-Path $env:LOCALAPPDATA 'doc-filer\watch'
    # No build servers left behind: a reused MSBuild node or compiler server outlives the
    # publish, and under a scheduled task it can surface as a stray console window.
    dotnet publish $project -c Release -o $published --nologo -v quiet -nodeReuse:false -p:UseSharedCompilation=false | Out-Null
    & (Join-Path $published 'doc-filer.exe') @Arguments
    exit $LASTEXITCODE
}

dotnet run --project $project -- @Arguments

exit $LASTEXITCODE
