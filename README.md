# doc-filer

Converts documents saved into a staging folder to markdown, and files them into a repo by
filename rules. Teams transcripts (.docx or .txt), OneNote pages saved as HTML, Word forms,
reports: name the file by a convention, save it to staging, and it lands in the right folder
of the repo under the right name.

It pairs with [teams-transcripts](https://github.com/gareth-jones-zen/teams-transcripts),
which fetches transcripts into the same staging folder. The two meet at the folder: the
fetcher knows nothing about the repo, and this tool knows nothing about Teams.

Until October 2026 it was `tools/console-runner` inside the repo it served.

## Running it

```powershell
./run.ps1 --config C:\path\to\doc-filer.json              # file whatever is new
./run.ps1 --config C:\path\to\doc-filer.json --dry-run    # say what it would do, write nothing
./run.ps1 --config C:\path\to\doc-filer.json --self-test  # check the routing rules
./run.ps1 --config C:\path\to\doc-filer.json --watch --log C:\path\to\doc-filer.log
```

Needs the **.NET 10 SDK** and nothing else. `run.ps1` is a thin wrapper over `dotnet run`, so
the first run compiles and later runs are quick.

**pandoc comes with the tool.** `vendor/pandoc-3.9-windows-x86_64.zip` holds pandoc 3.9 for
Windows. It's zipped because `pandoc.exe` (221MB) is over GitHub's 100MB file limit, and the
zip is about 40MB. `run.ps1` unpacks it into `vendor/` on first use (git-ignored) and the tool
uses it, unless the config's `pandoc` names another copy. **To upgrade:** zip the new release's
Windows files the same way, named `pandoc-<version>-windows-x86_64.zip`, and remove the old
zip; the newest by name is used. pandoc is GPL-2.0-or-later, by John MacFarlane and
contributors: its licence travels inside the zip (`COPYING.rtf`, `COPYRIGHT.txt`), and the
source is at <https://github.com/jgm/pandoc/releases/tag/3.9>. Without `--config` it uses
`$env:DOC_FILER_CONFIG`, then `doc-filer.json` in the current folder.

Exit code `0` on success, `1` if any file failed to convert, `2` on a configuration error,
`3` if another run with the same config didn't finish within 10 minutes. Check the summary
line, because a failure part-way through is easy to scroll past:

```
Processing complete: 4 converted, 4 copied, 346 unchanged, 1 failed
```

**Every run also lists staging files no rule matches**, as `WARNING: No rule matches …`. A
misnamed file (`catch up` for `catch-up`, `-part-1` for `-part1`, a double space) otherwise
produces no error and no output, which is the worst failure an ingest tool has.

## How it works

Two stages, and the order matters:

1. **Convert rules** turn each staging file into markdown in a `markdown/` folder inside
   staging, renamed by the rule.
2. **Copy rules** copy the finished markdown, and any originals worth keeping (PDFs,
   decks), into the repo.

A botched conversion never leaves a half-written file in the repo, and re-running is cheap:
a file whose source hasn't changed since last time is skipped, and so is a copy whose
content is identical to what's already there.

## Watch mode

`--watch` stays running and files whatever lands in staging, so nothing waits for someone
to remember to run it.

- **It lists the folders the rules read from on a timer** (`checkEverySeconds`, default 30)
  and compares sizes and modified times. Windows' file-change events aren't used: on
  OneDrive-synced folders they're unreliable.
- **It waits for the folders to stop changing** (`settleSeconds`, default 60) before a run,
  so a file still syncing isn't caught half-written.
- **It runs the after-run hook** whenever something was written, failed, or a *new* file
  matches no rule. Each unmatched file is reported once, not on every start; what's been
  reported is kept in `%LOCALAPPDATA%\doc-filer\unmatched-<key>.txt`.
- **Config edits take effect without a restart.** A broken edit is logged, and the old
  config stays in use.
- **It logs `alive` once an hour**, so a silent log means a stuck watcher, not a quiet day.
- **It runs a published copy** (`%LOCALAPPDATA%\doc-filer\watch\`), so `run.ps1` can still
  rebuild for one-off runs while it's running.
- **One run at a time per config.** A one-off run waits for a watch run to finish, and the
  other way round, through a named mutex that Windows releases if the process dies.

### Starting it at logon: `autostart`

```powershell
./run.ps1 autostart on --config C:\path\to\doc-filer.json   # watch at every logon, and start it now
./run.ps1 autostart status
./run.ps1 autostart off
```

The same shape as teams-transcripts' `autostart`.
- **`on` registers a Task Scheduler task, `doc-filer watch`,** that runs as you, only while you're logged on, and needs no admin rights.
- **The task:**
  - catches up a missed start;
  - restarts the watcher if it crashes (three times, five minutes apart);
  - is never stopped for running too long.
- **`--log <path>`** sets the log; the default is `%LOCALAPPDATA%\doc-filer\doc-filer.log`. Running `on` again with another config replaces the task.
- **`status`** shows whether the watcher is running, when it last started, and how it last ended.
- **`off`** stops it and removes the task.

**It runs under the headless console host (`conhost.exe --headless`), so no window ever appears.** It isn't run with `powershell -WindowStyle Hidden`, which hides PowerShell's window only after it has opened. A window can still appear, and **closing it stops the watcher.** `status` reports that case as "stopped: its console window was closed".

**One watcher per config.** Don't also start one some other way.

## The after-run hook

The tool knows nothing about git, or about what the repo wants done with new files. A
config's `afterRun` names a command that does:

```jsonc
"afterRun": ["python", "tools/doc_filer_hook.py"]
```

It runs from the repo root, in watch mode always and after a one-off run only with
`--hook`, whenever the run wrote into the repo, failed a file, or found a new unmatched
file. It gets a JSON report on stdin:

```json
{
  "config": "C:\\…\\doc-filer.json",
  "repoRoot": "C:\\…\\my-repo",
  "written": ["meetings/catch-ups/jane-smith/2026-10-20.md"],
  "failed": ["C:\\…\\Staging\\Notes\\Broken file 2026-10-20.docx"],
  "unmatched": ["C:\\…\\Staging\\Notes\\Jane Smith catch up 2026-10-21.docx"],
  "summary": "1 converted, 1 copied, 680 unchanged, 1 failed"
}
```

`written` is relative to the repo root. Its output goes to the log, prefixed `hook:`. A
typical hook commits the `written` paths and nothing else, and notifies someone about
`failed` and `unmatched`.

## The config

All behaviour lives in one JSONC file (comments allowed), kept with the repo it serves, not
here. See [examples/doc-filer.example.json](examples/doc-filer.example.json).

| Setting | What it is |
|---|---|
| `sourceRoot` | The staging folder, absolute |
| `repoRoot` | The repo, relative to the config file |
| `pandoc` | pandoc's executable, relative to the config file. Empty means the bundled copy (or `pandoc` on the PATH if there's none) |
| `people` | `{ "initials", "destination" }` for each person whose files are named by initials. Each is expanded through the person templates |
| `personConvertTemplates`, `personCopyTemplates` | Rules generated once per person, with `{initials}` and `{destination}` |
| `convert`, `copy` | Everything else |
| `afterRun` | The hook command, above |
| `watch` | `checkEverySeconds`, `settleSeconds` |
| `selfTest` | Routing checks, below |

**Rules are all-match, not first-match.** A filename two convert rules match is converted
twice, to both destinations. Use negative lookaheads to carve exceptions out of a general
rule, and add a self-test case for each.

**Convert rules** match a regex against the filename without its extension, scanning `from`
(relative to `sourceRoot`, not recursive):

| Group | Meaning |
|---|---|
| `(?<date>)` | required: `yyyy-mm-dd` or `yyyy-mm` |
| `(?<label>)` | optional: topic, person or series name |
| `(?<part>)` | optional: the `-part2` suffix on a split recording |

`to` (relative to `from`) and `name` are templates over `{date}`, `{label}` (spaces
underscored), `{label-slug}` (kebab-case) and `{part}`. Matching ignores case, because
hand-typed names drift.

Per-rule settings, all optional:

| Setting | What it does |
|---|---|
| `converter` | `Pandoc` (default) or `Copy`, for `.txt` that needs no conversion |
| `format` | pandoc output format, `markdown` by default. `gfm` for anything with tables: pandoc's markdown writes complex tables as grid tables, which render as a wall of `+---+` everywhere else |
| `inputFormat` | Overrides the format picked from the extension. `.html` is read as `html-native_divs-native_spans`, which strips a browser-saved page's layout wrappers; a OneNote rule sets `html`, because OneNote writes bold as a styled span |
| `luaFilter` | A pandoc Lua filter: a path relative to the config, or the name of one shipped in `filters/` |
| `wrap` | pandoc `--wrap`: `none` keeps one line per bullet |
| `prettify` | `false` switches off the Teams transcript clean-up. Use it for anything that isn't a transcript |

**Copy rules** copy files with matching `extensions` from `from` (relative to `sourceRoot`)
to `to` (relative to the repo). `recurse` keeps subfolders, which is how per-person and
per-series folders come through.

## Self-test

`--self-test` checks every `selfTest` case against the rules without touching any files:
the name must match exactly one rule and land where expected.

```jsonc
{ "from": "Notes", "file": "AB 1-2-1 2026-08-19-part1.docx",
  "folder": "markdown/AB/one-to-ones", "name": "2026-08-19-part1.md",
  "why": "Split recordings keep their suffix" }
```

**Run it after every config change.** A name matching two rules is the failure most likely
to file something in the wrong place without anyone noticing.

## Shipped filters

| Filter | For |
|---|---|
| `flatten-tables.lua` | Word forms built from layout tables. Expands merged cells, flattens each cell to one line and unwraps one-column boxes, so the result is pipe tables, not raw HTML. Content-preserving: **verify a change against the plain-text word set of real forms**, because `blocks_to_inlines` silently drops nested lists |
| `onenote-clean.lua` | OneNote pages saved as HTML. Keeps bold, drops styling spans, removes U+FFFC. Needs `"inputFormat": "html"` |

## The transcript clean-up

`TranscriptPrettifier` tidies pandoc's markdown from a Teams .docx (stray escapes, media
placeholders, speaker names split across lines). **Its steps run unchanged and in the same
order** as the original implementation: every transcript already filed was produced by
them, so a change would show up as a diff across all of them the next time one was
reprocessed. Add new steps at the end.

## Layout

```
run.ps1                     entry point
src/
  Program.cs                arguments, config discovery
  FilerConfig.cs            config model, person-template expansion, validation
  DirectoryProcessor.cs     the two stages; unmatched-file check
  Filer.cs                  one locked run, then the hook
  Watcher.cs                watch mode
  AfterRunHook.cs           runs the hook with the JSON report
  RunLock.cs                one run at a time per config
  FileNameMapper.cs         filename → folder and name
  TranscriptPrettifier.cs   Teams markdown clean-up
  SelfTest.cs               routing checks from the config
filters/                    shipped pandoc Lua filters
vendor/                     pandoc, zipped; unpacked here by run.ps1
examples/                   an example config
```
