<#
.SYNOPSIS
    Converts documents in a staging folder to markdown and files them into a repo.

.EXAMPLE
    ./run.ps1 --config C:\path\to\doc-filer.json
    ./run.ps1 --config C:\path\to\doc-filer.json --dry-run
    ./run.ps1 --config C:\path\to\doc-filer.json --watch --log C:\path\to\doc-filer.log
    ./run.ps1 autostart on --config C:\path\to\doc-filer.json     # watch at every logon, and start now
    ./run.ps1 autostart status
    ./run.ps1 autostart off
#>
[CmdletBinding()]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments
)

$ErrorActionPreference = 'Stop'

# With no arguments, $Arguments is $null, and splatting it passes one empty argument along.
$Arguments = @($Arguments | Where-Object { $_ })

$project = Join-Path $PSScriptRoot 'src\DocFiler.csproj'

# ---- autostart: watch mode at every logon, through Task Scheduler --------------------------
# The same shape as teams-transcripts' autostart: a task that runs as you, only while you're
# logged on, needs no admin rights, catches up a missed start, restarts after a crash, and is
# never stopped for running too long. It runs under the headless console host, so no window
# appears. A window that can be closed is a watcher that can be stopped by accident: that
# happened on 29 Oct 2026 under "powershell -WindowStyle Hidden".
$TaskName = 'doc-filer watch'

function Get-Option([string[]]$list, [string]$name) {
    $i = [array]::IndexOf($list, $name)
    if ($i -ge 0 -and $i + 1 -lt $list.Count) { return $list[$i + 1] }
    return $null
}

function Resolve-Config([string[]]$list) {
    $config = Get-Option $list '--config'
    if (-not $config) { $config = $env:DOC_FILER_CONFIG }
    if (-not $config -and (Test-Path 'doc-filer.json')) { $config = 'doc-filer.json' }
    if (-not $config) { throw 'autostart on needs a config: pass --config <path>, set DOC_FILER_CONFIG, or run from a folder holding doc-filer.json.' }
    if (-not (Test-Path $config)) { throw "Config not found: $config" }
    return (Resolve-Path $config).Path
}

# Stopping the scheduled task ends only the console host it started, not the watcher under it, so a
# restart used to leave the old watcher running alongside the new one, on old code (found 30 Oct 2026:
# three running). This stops any doc-filer watcher on the same config, wherever it was started from.
function Stop-Watcher([string]$config) {
    $wanted = [IO.Path]::GetFullPath($config)
    Get-CimInstance Win32_Process -Filter "Name='doc-filer.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -match '--watch' -and $_.CommandLine.IndexOf($wanted, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
        ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $_.ProcessId -Timeout 15 -ErrorAction SilentlyContinue
        }
}

function Describe-Result($code) {
    switch ($code) {
        0          { 'ok' }
        267009     { 'running' }
        267011     { 'never run' }
        3221225786 { 'stopped: its console window was closed (0xC000013A)' }
        default    { 'exit ' + $code }
    }
}

if ($Arguments.Count -ge 1 -and $Arguments[0] -eq 'autostart') {
    $verb = if ($Arguments.Count -ge 2) { $Arguments[1] } else { 'status' }
    $rest = if ($Arguments.Count -gt 2) { $Arguments[2..($Arguments.Count - 1)] } else { @() }
    $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue

    switch ($verb) {
        'on' {
            $config = Resolve-Config $rest
            $log = Get-Option $rest '--log'
            if (-not $log) { $log = Join-Path $env:LOCALAPPDATA 'doc-filer\doc-filer.log' }
            $log = [IO.Path]::GetFullPath($log)
            $me = "$env:USERDOMAIN\$env:USERNAME"
            $script = Join-Path $PSScriptRoot 'run.ps1'
            $argsLine = "--headless powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$script`" --watch --config `"$config`" --log `"$log`""
            $action = New-ScheduledTaskAction -Execute 'conhost.exe' -Argument $argsLine -WorkingDirectory (Split-Path $config)
            $trigger = New-ScheduledTaskTrigger -AtLogOn -User $me
            $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
                -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 5) -MultipleInstances IgnoreNew
            $principal = New-ScheduledTaskPrincipal -UserId $me -LogonType Interactive -RunLevel Limited
            if ($task) { Stop-ScheduledTask -TaskName $TaskName }
            Stop-Watcher $config
            Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal `
                -Description "Files whatever lands in the staging folder (doc-filer --watch). Config: $config. Log: $log" -Force | Out-Null
            Start-ScheduledTask -TaskName $TaskName
            Write-Output "Autostart is on: '$TaskName' runs at every logon, and has started now."
            Write-Output "  config  $config"
            Write-Output "  log     $log"
            exit 0
        }
        'off' {
            if (-not $task) { Write-Output 'Autostart is already off.'; exit 0 }
            Stop-ScheduledTask -TaskName $TaskName
            if ($task.Actions[0].Arguments -match '--config "([^"]+)"') { Stop-Watcher $Matches[1] }
            Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
            Write-Output "Autostart is off: '$TaskName' is stopped and removed."
            exit 0
        }
        'status' {
            if (-not $task) { Write-Output 'Autostart is off.'; exit 0 }
            $info = Get-ScheduledTaskInfo -TaskName $TaskName
            $running = @(Get-Process doc-filer -ErrorAction SilentlyContinue).Count -gt 0
            Write-Output "Autostart is on ('$TaskName')."
            Write-Output "  state        $($task.State)$(if ($task.State -ne 'Running' -and -not $running) { ' (the watcher is not running)' })"
            Write-Output "  last run     $(if ($info.LastTaskResult -eq 267011) { 'never' } else { $info.LastRunTime })"
            Write-Output "  last result  $(Describe-Result $info.LastTaskResult)"
            Write-Output "  command      $($task.Actions[0].Execute) $($task.Actions[0].Arguments)"
            exit 0
        }
        default {
            Write-Error "Unknown autostart command '$verb'. Use: autostart on [--config <path>] [--log <path>] | off | status"
            exit 2
        }
    }
}

# pandoc ships with the tool, zipped (pandoc.exe is over GitHub's 100MB file limit), and is
# unpacked here on first use, so nobody has to install it. A config that names its own
# pandoc still wins; this is the default.
$pandocZip = Get-ChildItem (Join-Path $PSScriptRoot 'vendor') -Filter 'pandoc-*-windows-x86_64.zip' |
    Sort-Object Name | Select-Object -Last 1
if ($pandocZip) {
    $pandocDir = Join-Path $PSScriptRoot ('vendor\' + $pandocZip.BaseName)
    if (-not (Test-Path $pandocDir)) {
        # Unpack beside the target and rename, so an interrupted unpack never looks complete.
        $staging = "$pandocDir.unpacking"
        if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
        Expand-Archive -Path $pandocZip.FullName -DestinationPath $staging
        Rename-Item $staging (Split-Path $pandocDir -Leaf)
    }
    # pandoc's official zip puts its files in a pandoc-<version>\ subfolder; find the exe wherever it is.
    $pandocExe = Get-ChildItem $pandocDir -Recurse -Filter 'pandoc.exe' | Select-Object -First 1
    if ($pandocExe) { $env:DOC_FILER_PANDOC = $pandocExe.FullName }
}

if ($Arguments -contains '--watch') {
    # Watch mode runs a published copy, not the build output, so `dotnet run` can still
    # rebuild for one-off runs while the watcher is running (it would otherwise find the
    # build output locked). If publishing fails because the copy is already running,
    # the existing copy is used.
    $published = Join-Path $env:LOCALAPPDATA 'doc-filer\watch'
    # One watcher per config: a new one replaces any already running, which also frees the
    # published copy so it can be rebuilt with the current code.
    $watchConfig = Get-Option $Arguments '--config'
    if ($watchConfig) { Stop-Watcher $watchConfig }
    # No build servers left behind: a reused MSBuild node or compiler server outlives the
    # publish, and under a scheduled task it can surface as a stray console window.
    dotnet publish $project -c Release -o $published --nologo -v quiet -nodeReuse:false -p:UseSharedCompilation=false | Out-Null
    & (Join-Path $published 'doc-filer.exe') @Arguments
    exit $LASTEXITCODE
}

dotnet run --project $project -- @Arguments

exit $LASTEXITCODE
