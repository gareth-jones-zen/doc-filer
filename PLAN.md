# doc-filer on .NET 11, Native AOT and WinBackground: the switch-over

The `dotnet` branch makes doc-filer the same shape as the other tools: two Native AOT exes (`doc-filer.exe` and the
windowless `doc-filer-background.exe`) on background-tools-lib's WinBackground, installed from a release zip into
`%LOCALAPPDATA%\Programs\doc-filer`, with `autostart` in the exe. Until it's switched, the live watcher is the old
one: the `doc-filer watch` task runs `conhost.exe --headless powershell.exe … run.ps1 --watch …` from the
`~/source/repos/doc-filer` checkout (main), and `run.ps1` runs `dotnet publish` at every start.

## What changed, and what didn't

| | Before | Now |
|---|---|---|
| Build | net10 (then net11 on this branch), framework-dependent, published by `run.ps1` at every logon | net11.0-windows, Native AOT, self-contained, built once by the release workflow |
| Programs | `doc-filer.exe` under conhost and PowerShell | `doc-filer.exe` (console) and `doc-filer-background.exe` (WinExe, what the task runs) |
| pandoc | `vendor/` zip, unpacked by `run.ps1`, named in `DOC_FILER_PANDOC` | unpacked by the build into `pandoc\` beside the exes; `DOC_FILER_PANDOC` still wins if set |
| Autostart | `run.ps1 autostart on|off|status` (PowerShell) | `doc-filer autostart on|off|status` (WinBackground `ScheduledTask`), **same task name `doc-filer watch`**, same settings |
| Run lock | .NET `Mutex` `Local\doc-filer-{KEY}` | WinBackground `NamedLock`, **same name** (16 upper-case hex, doc-filer's own key) |
| One watcher per config | `run.ps1` killed the old watcher by command line; the exe had no lock | `Local\doc-filer-watch-{KEY}` single-instance lock; a second watcher logs a warning and exits `4`. `autostart on/off` also stop any other watcher on the config |
| Hook | own `Process` code | WinBackground `Shell.Run` (stdin, working folder, UTF-8, kill tree on timeout). Payload bytes unchanged |
| `pdf`'s pandoc and Edge | own `Process` code | `Shell.Run` |
| Log | own `Log` | **unchanged** (`yyyy/MM/dd HH:mm:ss msg`, `WARNING:`/`ERROR:`, CRLF). WinBackground's `Logger` writes `yyyy-MM-dd HH:mm:ss  msg`, so it isn't used |
| Notifications | none (the EA's Python hook notifies) | none |
| `--version` | none | from the build (`1.0.0-dev`, or the tag's version in a release) |

**Behaviour that differs, all deliberate:**
- A second watcher on the same config now exits (code 4) instead of replacing the running one.
- The hook's output is decoded as UTF-8 (`Shell.Run`'s `utf8`), whatever the console. The report on stdin is the same bytes as before (ASCII, CRLF).
- A hook that exits without reading stdin is no longer logged as "After-run hook failed: broken pipe".
- `autostart status` says "last result ok/exit N (meaning)"; it reads the watch lock to say whether the watcher is running when the task isn't.
- pandoc's conversion runs are deliberately **not** on `Shell.Run`: it turns pandoc's CRLF into LF, which would change pandoc warnings in the log. They keep doc-filer's own process code.

## Parity, old against new (13 Nov 2026)

Old = `origin/main` (6e7b2ba, the live code), built in scratch with the .NET 10.0.402 SDK, plus only the hidden
`--dump-config` diagnostic for the config comparison. New = this branch, published Native AOT. All on copies under
`%TEMP%`, never the real staging folder or the repo; no document content printed; scratch deleted afterwards.

| Check | Result |
|---|---|
| `--self-test`, the real config | byte-identical (34 lines, 32 cases) |
| `--dump-config`, the real config | byte-identical (1,317 lines; 54 convert and 25 copy rules after expansion) |
| Full re-file: a copy of every staging folder the rules read (381 files), into empty scratch repos seeded with a copy of the preps folder plus synthetic archive cases | both `374 converted, 381 copied, 0 unchanged, 0 failed, 1 archived`; staging trees identical (755 files), repo trees identical (410 files; only the side-specific config differs); **hook report byte-identical** (23,147 bytes, CRLF, pure ASCII; 381 written, 1 moved); **log identical** (765 lines, timestamps stripped) except the hook's own output, below |
| Hook output decoding | the old build decoded the hook's output in the console's code page (850 under conhost: UTF-8 `café –` became `caf├® ÔÇô`); the new one reads UTF-8 |
| `pdf` intermediate HTML, 4 documents (up to 131 images shrunk through GDI+ under AOT) | byte-identical; PDF sizes equal; console output identical |
| Watch mode, by hand on a scratch config with the windowless exe | first run, unmatched file reported once, a new file filed after the settle, a corrupt .docx retried 5 minutes apart and reported to the hook only on its fourth try (that try came early, at the run a config edit set off, as it always has), the config edit reloaded, a second watcher exits 4 |

## Switch-over (the main session and GJ)

**Order matters:** the live checkout `~/source/repos/doc-filer` must still hold `run.ps1` when the old task is turned
off, so don't pull it to the new `main` until step 6.

### 1. Release (main session)

1. In `~/source/repos/doc-filer-dotnet`: tag the old `main` (6e7b2ba) as **`v0.1.0`** (the rollback point: `run.ps1`
   and the framework-dependent build), and push the tag.
2. Fast-forward `main` to `dotnet` (`git push origin dotnet:main`), delete `dotnet` locally and on GitHub. CI
   (`build.yml`) builds `main` only, so this push is the first build of the final commit; check it's green.
3. Tag **`v1.0.0`** on `main` and push it. The release workflow tests, publishes, packages
   `doc-filer-1.0.0-win-x64.zip` (both exes, `filters\`, `styles\`, `pandoc\`, README) and attests it. The repo is
   **public** (not private, as the EA's notes say), so the provenance attestation works.
4. Download and check: `gh release download v1.0.0 -R gareth-jones-zen/doc-filer -p '*.zip' -p SHA256SUMS.txt`,
   `sha256sum -c` (zip line), `gh attestation verify <zip> -R gareth-jones-zen/doc-filer`.
5. Unzip into **`%LOCALAPPDATA%\Programs\doc-filer`** (new folder; nothing is running from it yet).
6. Smoke test, all read-only:
   - `doc-filer.exe --version` → `1.0.0`;
   - `doc-filer.exe --self-test --config tools/doc-filer.json` → "All 32 filename cases passed." (identical to the old);
   - `doc-filer.exe --dry-run --config tools/doc-filer.json` → the same `[dry-run]` and `No rule matches` lines as the old build's dry run (strip timestamps).

### 2. The EA's wrapper (main session)

`tools/doc-filer.ps1` calls the installed exe instead of `run.ps1`:

```powershell
$tool = Join-Path $env:LOCALAPPDATA 'Programs\doc-filer\doc-filer.exe'
if (-not (Test-Path $tool)) {
    Write-Error "doc-filer isn't installed at $tool. Unzip a release (https://github.com/gareth-jones-zen/doc-filer/releases) there."
    exit 2
}
$config = Join-Path $PSScriptRoot 'doc-filer.json'

if ($Arguments.Count -ge 1 -and $Arguments[0] -eq 'pdf') { & $tool @Arguments; exit $LASTEXITCODE }

if ($Arguments.Count -ge 1 -and $Arguments[0] -eq 'autostart') {
    if ($Arguments.Count -ge 2 -and $Arguments[1] -eq 'on') {
        & $tool autostart on --config $config --log $log   # the EA's log, as its autostart on passes today
    } else {
        & $tool @Arguments
    }
    exit $LASTEXITCODE
}

$extra = @()
if (-not ($Arguments -match '^--(dry-run|self-test|watch|hook|version)$|^-n$')) { $extra = @('--hook') }
& $tool --config $config @extra @Arguments
exit $LASTEXITCODE
```

The old `\Claude EA\Doc filer` legacy-task removal can go. The header comment should say the exe is installed from a
release into `%LOCALAPPDATA%\Programs\doc-filer`. One-off runs no longer compile anything, so they're instant.

### 3. Swap the task (GJ, at the console: registering a task needs him)

```
! powershell -NoProfile -ExecutionPolicy Bypass -File ~/source/repos/doc-filer/run.ps1 autostart off
! powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/doc-filer.ps1 autostart on
```

- **The first is the old `run.ps1`**: it stops the task (which ends only `conhost.exe`), then kills `doc-filer.exe
  --watch` on the EA config by command line (its `Stop-Watcher`), then removes the task. **That's the conhost
  hazard handled by the old code that knows about it.**
- **The second registers the new task** `doc-filer watch` → `%LOCALAPPDATA%\Programs\doc-filer\doc-filer-background.exe
  --watch --config "…\tools\doc-filer.json" --log "<the EA's log>"`, working folder `tools\`,
  and starts it. **It also stops any doc-filer watcher on that config first**, either program, however started, so
  even if the first step were skipped the old `doc-filer.exe` wouldn't be left running beside the new one. And the
  run lock has the same name in both, so even an overlap could only serialise runs, never file twice at once.

### 4. Verify (main session)

1. **One watcher, the new one:**
   ```powershell
   Get-CimInstance Win32_Process -Filter "Name='doc-filer.exe' OR Name='doc-filer-background.exe' OR Name='conhost.exe' OR Name='powershell.exe'" |
     Where-Object { $_.CommandLine -match 'doc-filer' } | Select-Object ProcessId, Name, CommandLine
   ```
   Exactly one `doc-filer-background.exe --watch`; no `doc-filer.exe`, no `conhost`/`powershell` running `run.ps1`.
2. `./tools/doc-filer.ps1 autostart status` → state `Running`, the command is the background exe.
3. **The log** (the EA's log, unchanged): a `Watching … (every 30s, settle 60s; config …)` line at the
   switch time, then `Processing complete: 0 converted, 0 copied, N unchanged, 0 failed`; **an `alive` line an hour
   later.** `tools/session-start.py`'s watcher check reads the same file and format.
4. **A test file filed and committed by the hook:** save a small PDF as
   `<staging>\Briefings\Ad-hoc\doc-filer-switch-over-test.pdf`. About 90 seconds later (30 s poll, 60 s settle)
   the log shows it copied to `briefings/ad-hoc/`, `After-run hook: python tools/doc_filer_hook.py` and the hook's
   lines, and `git log -1` shows the hook's commit of that one path. **Then delete the staging file first, and only
   then `git rm` the repo copy and commit**, or the next run copies it back.
5. `python tools/restart-watchers.py doc-filer` (optional): with the exe run directly, the stop now waits for the real
   process, and the new watcher starts cleanly.

### 5. The EA's records (main session)

- **The EA's notes on ingest:**
  - "doc-filer is its own tool since 29 Oct 2026 (… **private**; checked out at `~/source/repos/doc-filer`)" → **public**,
    installed from a release at `%LOCALAPPDATA%\Programs\doc-filer` (the checkout is for development only);
  - "**It runs under `conhost --headless`**, so no window appears. On 29 Oct 2026 a console window appeared…" → the
    task runs `doc-filer-background.exe` directly: no window, and stopping the task stops the watcher;
  - "Registering a task needs GJ … `! powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/doc-filer.ps1 autostart on`" stays true;
  - add: a second watcher on the same config exits (code 4); `--version`;
  - "**`--self-test` cases live in `src/SelfTest.cs`, not the config, so adding one needs a rebuild**" is wrong (they
    live in the config's `selfTest`, and `SelfTest.cs` only runs them); correct it while there;
  - upgrade: `autostart off`, unzip the new release over the folder, `autostart on`.
- **`tools/doc-filer.json` header comment:** "checked out at ~/source/repos/doc-filer" and `"pandoc": ""   // the copy
  bundled with doc-filer (its vendor/ folder)` → installed at `%LOCALAPPDATA%\Programs\doc-filer`; the copy beside the
  exe. Comment-only; the watcher reloads it and logs "Config changed; reloaded".
- **Ports log** (`strategy/dotnet-ports/README.md`): the switch-over entry.

### 6. Tidy (main session)

- Remove the old checkout `~/source/repos/doc-filer` (check it's clean and nothing unpushed), and rename
  `~/source/repos/doc-filer-dotnet` to `~/source/repos/doc-filer` (now on `main`).
- Remove the old published copy `%LOCALAPPDATA%\doc-filer\watch\` (the old watcher ran from it). **Keep**
  `%LOCALAPPDATA%\doc-filer\unmatched-*.txt`: the new watcher reads the same file, so nothing is re-reported.

## Rollback

```
! powershell -NoProfile -ExecutionPolicy Bypass -File ./tools/doc-filer.ps1 autostart off
```
(the new exe: stops and removes the task, and any watcher on the config), then restore the old checkout at tag
`v0.1.0` and its `run.ps1`:

```
git -C ~/source/repos/doc-filer checkout v0.1.0
! powershell -NoProfile -ExecutionPolicy Bypass -File ~/source/repos/doc-filer/run.ps1 autostart on --config "<EA>\tools\doc-filer.json" --log "<the EA's log>"
```

and put back the old `tools/doc-filer.ps1` (`git revert` of step 2). The run lock and the unmatched-file record are
the same in both, so going back and forth loses and repeats nothing.

## Decisions for GJ

1. **Straight to `v1.0.0`**, as for the other tools, rather than an RC. Recommended: yes.
2. **Release assets:** the zip (about 45MB, since pandoc is in it) plus the two bare exes and `SHA256SUMS.txt`, as
   mail-triage's. The bare exes don't work alone (they need `pandoc\` and `filters\` beside them); dropping them from
   the release is an option. Recommended: keep, for parity with the other tools and the attestation.
3. **The repo is public**, though the EA's notes say private. It holds no EA data (the config lives in the EA repo), and
   pandoc's GPL licence travels with the zip. Recommended: leave it public and correct the notes.
