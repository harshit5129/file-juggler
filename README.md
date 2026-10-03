<img src="src/Juggler.Ui/Assets/Icon.png" width="96" alt="File Juggler emblem">

# File Juggler

**A low-memory file automation tool for Windows.** It watches folders in the
background and moves, renames, copies or deletes files according to rules you
define, silently, all day, without ever stealing focus or getting in your way.

> **Status: pre-alpha.** The rule engine, configuration layer and rule editor
> work and are tested. The resident daemon does not exist yet, so **no rule is
> ever applied to a real file.** The editor can only be trusted with a config
> you are happy to have validated. See [Status](#status) for exactly what is
> and is not done.

---

## Download

Prebuilt Windows binaries are attached to each
[GitHub release](https://github.com/harshit5129/file-juggler/releases).

| Asset | Size | Use |
| --- | --- | --- |
| `FileJuggler-<version>-win-x64-setup.exe` | ~44 MB | **Recommended.** Installs with Start Menu shortcut, Add/Remove Programs entry and an uninstaller. |
| `FileJuggler-<version>-win-x64.zip` | ~43 MB | Portable. Extract and run the exe. |

Both contain a single self-contained executable: **no compiler, no SDK, and no
.NET runtime install required.**

### Installing

Run `FileJuggler-<version>-win-x64-setup.exe`. It is a **per-user install**:
it writes to `%LOCALAPPDATA%\Programs\FileJuggler` and never raises a UAC
prompt, because the tool needs no elevation. Config lives at
`%LOCALAPPDATA%\FileJuggler\rules.json` and is created on first launch.

The installer offers three optional tasks:

| Task | Default | Notes |
| --- | --- | --- |
| Start Menu shortcut | on | Carries the emblem |
| Desktop shortcut | off | Carries the emblem |
| Start automatically when I sign in | **off** | See below |

**Autostart is off by default, deliberately.** Autostarting the *editor* would
open a window at every sign-in, which is exactly the behaviour this tool exists
to avoid. The process that should autostart is the resident daemon, which is not
built yet. The mechanism is in place and tested; the default will flip to on when
the daemon lands and the entry is repointed at `juggler.exe`.

Uninstalling never deletes your config. It holds your rules and real filesystem
paths, so it is left in place and the uninstaller tells you where it is.

### A note on binary size

These builds are **self-contained** rather than NativeAOT. NativeAOT would give
a far smaller and faster binary, which is what the resident daemon will
eventually use, but it requires the Visual Studio C++ build tools and Windows
SDK and fails the build outright without them. The editor is an on-demand
process, so its size and idle footprint are not the thing being optimised here.

To build either artifact yourself:

```powershell
tools\pack.ps1 -Version 0.1.0
```

Requires [Inno Setup 6](https://jrsoftware.org/isdl.php) for the installer.

## Status

| Component | State |
| --- | --- |
| Rule engine, conditions, actions, config validation | Working, 22 passing tests |
| Config store: atomic writes, hot reload, validation | Working |
| Rule editor UI (Avalonia, on-demand process) | Working |
| Resident daemon: tray, watcher, executor, reconciler | **Not started** |
| Autostart, battery/lock policy, log rotation | **Not started** |
| Memory / soak budget harness | **Not started** |

Nothing in this repository has yet touched a file on your disk. There is no code
path that moves, copies or deletes anything.

## Why this exists

Most file automation tools on Windows fail in one of two ways.

**They are heavy.** A resident tray process that quietly holds 150-400 MB while
doing nothing is not "background software" - it is a memory leak with a UI. You
notice it on a 16 GB laptop, and you notice it on a 32 GB desktop.

**They disturb you.** Balloon notifications for routine work. A window that
steals focus. CPU spinning because nobody set a priority class. Activity that
keeps the disk awake on battery.

File Juggler is built backwards from those two complaints. The targets below are
design goals, **not measurements** - they become enforceable only once the
daemon exists and the budget harness is built.

| Goal | Target |
| --- | --- |
| Idle resident memory, tray visible, 10 watched roots | <= 20 MB working set |
| Burst: 10,000 files dropped in 60 s | <= 80 MB peak, back to baseline in 60 s |
| 12-hour soak | Working set never trends upward |
| Focus theft | Zero. The window never calls `SetForegroundWindow` |
| Notifications | Off by default |
| Content reading | Never. File contents are not opened |

## Design

```
+-- always resident ---------------------+   +-- only while you edit rules ------+
|  juggler.exe   NativeAOT console       |   |  Juggler.Ui.exe   Avalonia       |
|  no UI framework, no .NET runtime      |<->|  starts on demand, exits on close |
|  ~8-20 MB                             IPC|  ~50 MB, 0 MB when closed          |
|  tray . watch . evaluate . apply       |   |                                  |
+-----------------------------------------+   +----------------------------------+
```

Two decisions carry most of the weight.

### 1. The resident process carries no UI framework

The tray icon is Win32 `Shell_NotifyIcon` plus a hand-rolled
`GetMessage`/`DispatchMessage` pump: no WinForms, no WPF, no Avalonia. That
keeps `PublishAot` in NativeAOT's fully-supported case instead of fighting the
trimmer, and it is why the idle footprint can be single-digit megabytes rather
than triple digits.

This matters more than it sounds. WinForms and WPF assemblies **cannot be
trimmed** ([dotnet/winforms#9911](https://github.com/dotnet/winforms/issues/9911)),
so a UI framework in the resident process means either abandoning NativeAOT or
reaching for the unsupported `_SuppressWinFormsTrimError` escape hatch. A
console app is NativeAOT's canonical supported case.

The rule editor is a *separate* process that exists only while you are editing,
so its framework choice costs nothing at idle. It is written in Avalonia because
Avalonia is the only desktop stack that both looks modern and publishes under
NativeAOT.

### 2. Memory is bounded by backpressure, not by hope

The obvious implementation of a file watcher - a queue plus a worker thread -
scales its memory with *burst size*. Copy 10,000 files in and the queue grows to
hold every pending path while the slower worker falls behind.

```
  ReadDirectoryChangesW  ->  bounded Channel (cap 1024)  ->  executor
        64 KB buffer             drop-oldest on full          1 worker
```

When the channel fills, the root is marked dirty rather than queued, and a
reconciliation sweep later picks up what the OS coalesced or dropped. Peak
memory is therefore a constant - `capacity x max path length` - regardless of
whether 10 or 10 million files land in the folder.

The same mechanism is the safety net for events Windows drops on its own, which
happens on network shares and on buffer overflow.

### 3. Every action is dry-runnable, journaled and reversible

Software that moves your real files while you are not looking is a hazard.

- Every destructive action is appended to an **undo journal and flushed before it
  executes**.
- Deletes go to the **Recycle Bin**, never `unlink`.
- `--dry-run` prints exactly what would happen and touches nothing on disk.
- Config is validated in full before being applied; an invalid config leaves the
  running rules untouched instead of silently disabling them.
- A config that cannot be *read* is never overwritten. See
  [Configuration safety](#configuration-safety).

## Behaviour contract

The app must never be the reason you notice your computer.

- **Never in the taskbar.** The tray icon is the only persistent UI surface.
- **Never takes focus.** `SetForegroundWindow` is never called in any code path.
  The editor opens only on an explicit tray click. Calls to it,
  `BringWindowToTop`, or `SetActiveWindow` in the resident process are rejected
  by a CI grep check.
- **Never notifies unless asked.** Routine events are opt-in and default off.
  Failures never surface as dialogs: a rule that fails 400 times must not
  produce 400 interruptions.
- **Always yields.** `BELOW_NORMAL` process priority, dropping to `IDLE` when the
  tray is hidden and nothing is pending. Work is capped per scheduling slice so
  a burst cannot monopolise the disk.
- **Sleeps when you are away.** Suspends on session lock and screen saver;
  throttles on battery and monitor-off. Detected by event, not polling.
- **Exactly one instance.** A named mutex, because two copies would double the
  memory *and* run every rule twice.

## Running modes

Both are first-class, selected per user:

- **Resident** - tray icon, event-driven, reacts in milliseconds, holds the
  small footprint above.
- **Scheduled** - `juggler.exe --once` runs, applies everything pending, and
  exits, freeing all memory. Costs up to *N* minutes of latency, but the resident
  footprint is genuinely **zero**. Register it with Task Scheduler.

| | Resident | Scheduled |
| --- | --- | --- |
| Idle memory | 8-20 MB | **0 MB** |
| Reaction time | milliseconds | up to interval |
| Misses files created while off | No | Yes, caught by next sweep |
| CPU while idle | ~0 | None |

## Commands

| Command | Purpose |
| --- | --- |
| `juggler.exe` | Run in resident tray mode |
| `juggler.exe --once` | Run one pass and exit |
| `juggler.exe --dry-run` | Report what would happen; change nothing |
| `juggler.exe doctor` | Print diagnostic state; attach this to bug reports |
| `juggler.exe --config <path>` | Use an alternative config file |
| `juggler.exe --install-autostart` / `--uninstall-autostart` | Manage the per-user Run entry |
| `Juggler.Ui.exe` | Rule editor; launches on demand, exits when closed |

Only `Juggler.Ui.exe` exists today. The `juggler.exe` rows are the intended
interface.

## Configuration safety

The config file is the one piece of state the user owns directly, so two rules
are enforced in code and covered by tests:

1. **Enums are written as names, not numbers.** `"mode": "Resident"`, not
   `"mode": 0`. The config is meant to be hand-edited.
2. **A config that failed to load is never overwritten.** If the app cannot read
   the file, `Save()` returns an error instead of writing whatever it happens to
   hold in memory. It likewise treats a *missing* or *blank* file as
   non-authoritative rather than as "the user deleted everything", because an
   atomic save briefly makes the path unresolvable.

Without rule 2, opening the editor with a config it could not parse would
replace the user's rules with an empty list. That bug shipped once and is now a
regression test.

## Repository layout

```
src/Juggler.Core/       Rule engine, config, conditions, actions, journal.
                        IsAotCompatible=true. No UI, no Win32.
src/Juggler.Ui/         Avalonia rule editor. Never AOT, never resident.
tests/                  Rule engine, config-store and validator tests.
config/                 example.rules.json (committed); local.json never is.
```

Planned, not yet present: `src/Juggler.Daemon/` (entry point, tray, watcher,
executor, reconciler) and `tools/Juggler.Bench/` (the harness that gates CI on
the budgets above).

Dependency direction is strictly `Ui -> Daemon -> Core`, and `Core` depends on
nothing in the project.

## Rules

A rule is a **monitor** (folders to watch, optionally recursive), an **If**
(conditions that must all match), and a **Then** (what to do).

```jsonc
{
  "id": "screens01",
  "name": "Sort screenshots",
  "enabled": true,
  "monitor": {
    "paths": [ { "path": "C:\\Users\\me\\Pictures\\Screenshots" } ],
    "includeSubfolders": true
  },
  "if": {
    "extensions": ["png", "jpg", "jpeg"],
    "minSizeBytes": 10240,
    "namePattern": "^Screenshot \\d{4}",   // regex, or "Glob" for wildcards
    "namePatternKind": "Regex"
  },
  "then": {
    "action": "SortIntoFolders",
    "into": "C:\\Users\\me\\Pictures\\Sorted\\{extension}",
    "sortBy": "Extension"
  }
}
```

The shipped `config/example.rules.json` is covered by a test, so it cannot drift
away from the schema the code actually accepts.

Conditions in v1: extension, name glob, name regex, size range, created/modified
date range. Actions: move, copy, rename, sort into subfolders by
extension/name/date/size, delete to Recycle Bin, run a command from an allowlist.

First matching rule wins, so put specific rules above general ones. A 5-second
settle window means the daemon never grabs a half-copied file.

**Content-based conditions** - matching on text inside a PDF or `.docx` - are
deliberately not implemented. Reading document contents is the single largest
source of memory pressure in this class of tool, and it is the reason the memory
budgets above are achievable.

## Roadmap

1. Daemon skeleton - tray, message pump, single-instance mutex, priority class
2. Watcher core - `ReadDirectoryChangesW`, bounded channel, overflow to sweep
3. Executor - conditions, actions, undo journal, dry-run
4. Named-pipe bridge between editor and daemon
5. Autostart, battery/lock idle policy, log rotation
6. Budget harness and CI releases

Deferred: content-based conditions, cross-platform support, scheduled rule
triggers (daily/weekly runs), scripting.

## Known issues

- **Right-hand controls can clip.** On a 125% DPI display the window frame is
  created smaller than Avalonia lays out, so the trailing controls in the header,
  toolbars and rule rows can render past the window edge. Under investigation.

## Contributing

Issues and pull requests are welcome - see [CONTRIBUTING.md](CONTRIBUTING.md).
If you want to propose a rule condition or action, open an issue first so we can
discuss its memory cost before you write the code.

## Security

This tool moves and deletes files using your own permissions. Please read
[SECURITY.md](SECURITY.md) before reporting a vulnerability, and treat the
`config/` directory as sensitive: it contains real filesystem paths.

## License

[MIT](LICENSE)