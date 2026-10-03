# Architecture

How File Juggler is put together, and why.

For the memory and backgrounding behaviour specifically, read
[Performance](performance.md) and [Background operation](background-operation.md).
This document covers structure and data flow.

---

## Process split

Two executables, because one of them must be tiny and permanent while the other
must be comfortable and temporary.

```
???? juggler.exe ??? NativeAOT console, no UI framework ????????????????????
?                                                                          ?
?  Win32 message pump ?? tray icon, menu, session/power notifications      ?
?                                                                          ?
?  WatcherManager ??? bounded Channel (1024) ??? RuleExecutor (1 worker)   ?
?   ReadDirectoryChangesW    drop-oldest        evaluate + apply           ?
?   per root, 64 KB buffer   on overflow        journal every action       ?
?   depth-capped recursion                      dry-run aware              ?
?                                                                          ?
?  Reconciler ?? periodic sweep of dirty roots, catches dropped events      ?
?  ConfigStore ?? rules.json, hot-reloaded, validated on change            ?
?  Journal ?? undo records, appended before any destructive action         ?
?  Logger ?? rotating, hard-capped at 5 MB x 3                              ?
????????????????????????????????????????????????????????????????????????????
                          ? named pipe
                          ? length-prefixed JSON, request/response
???? Juggler.Ui.exe ??? WinForms, on demand, exits on close ????????????????
?  Rules tab ? Log tab ? Settings tab ? editor form                         ?
?  Starts only on explicit user request. Zero cost when closed.             ?
????????????????????????????????????????????????????????????????????????????
```

### Why the resident process has no UI framework

`Shell_NotifyIcon` and a `GetMessage`/`DispatchMessage` loop are a few hundred
lines of P/Invoke and nothing more. Carrying WinForms, WPF, or Avalonia to avoid
them would cost:

- **Memory.** A UI framework runtime is the bulk of the idle footprint. This is
  the entire reason the target is single-digit megabytes rather than triple
  digits.
- **Native AOT viability.** WinForms and WPF assemblies cannot be trimmed -
  `dotnet/winforms#9911` is the tracking issue - so a UI framework in the
  resident process means either giving up AOT or reaching for the unsupported
  `_SuppressWinFormsTrimError` escape hatch. A console app is NativeAOT's
  canonical supported case.

The editor has neither problem, because it is not resident. It uses WinForms
because it is the least ceremony for a dense form UI on Windows.

Full reasoning, including what was rejected: [ADR-0002](adr/0002-nativeaot-and-the-ui-framework-split.md).

---

## Threading model

| Thread | Owns | Why |
| --- | --- | --- |
| Main | Win32 message pump, tray icon, menu | The Win32 message pump must be on the thread that created the window |
| Watcher (one per root) | Overlapped `ReadDirectoryChangesW` | One blocked read per root; overlapped I/O needs a completion context per root |
| Executor (single) | Rule evaluation and file operations | Single worker keeps memory flat and avoids disk seek thrash. See [Performance](performance.md#cpu-budget). |
| Reconciler | Periodic directory sweeps | Time-based, independent of events |
| Named-pipe server | UI requests | Isolates UI-triggered work from the executor so the editor cannot stall sorting |

### Why a single executor thread

Two reasons, both measured rather than assumed:

1. **Memory.** One worker's in-flight state is bounded and constant. Two workers
   double it for no benefit.
2. **Mechanical disks.** Parallel random I/O makes a spinning disk seek
   thrash; throughput *drops*. Users still on spinning media would see the tool
   make their machine slower, which is the opposite of the goal.

`maxConcurrent` exists in config for NVMe and network storage, but defaults to
`1` for exactly this reason.

### Event pipeline

```
  filesystem
      ?
      ?
  ReadDirectoryChangesW  ???? overlapped, 64 KB buffer, depth-capped
      ?  batch of FILE_NOTIFY_INFORMATION entries
      ?
  decode + filter          ?? skip our own writes (see below), skip reparse points
      ?
      ?
  bounded Channel (1024)   ?? FULL ? mark root dirty, drop, keep going
      ?                        (this is what bounds memory under a burst)
      ?
  debounce (5 s settle)    ?? do not touch a file still being written
      ?
      ?
  rule evaluation          ?? conditions, first-match-wins per file
      ?
      ?
  journal append           ?? BEFORE the action, fsync'd
      ?
      ?
  apply action             ?? move / rename / copy / recycle-bin / execute
      ?
      ?
  Reconciler               ?? later, sweeps dirty roots for anything dropped
```

**The self-write filter matters.** After moving a file, the move itself raises
change notifications. Without filtering, each action would retrigger itself and
loop. Every path the daemon writes is recorded in a short-lived ignore set that
is checked and expired by timestamp.

**Debouncing before action.** A file that appeared 5 seconds ago may still be
being copied. Waiting for a 5 s settle window before acting means the daemon
never grabs a half-written file. This is why scheduled mode has a longer
latency than it strictly needs, and why resident mode is not truly instantaneous
either.

---

## Configuration loading

`rules.json` is watched for changes and hot-reloaded. A change is validated in
full before it is swapped in - an invalid config leaves the running rules
untouched and writes the parse error to the log. A tool that silently disables
your rules because of a stray comma would be worse than one that does nothing.

The editor writes to a temp file and atomically renames, so the daemon never
observes a partial write.

---

## Native AOT constraints

The resident process sets `PublishAot`, which forbids runtime code generation:

| Forbidden | Consequence here |
| --- | --- |
| `Assembly.Load` / dynamic loading | No plugin system. Everything is compiled in. |
| `Reflection.Emit` | No runtime codegen. |
| Built-in COM | We use P/Invoke to Win32 directly instead. |
| Most reflection-heavy serialization | `System.Text.Json` source generators are used, not reflection-based binding. |

`Juggler.Core` sets `IsAotCompatible=true` with `VerifyReferenceAotCompatibility`,
and CI treats AOT analyzer warnings as **errors**. A dependency that would break
the resident process fails the build rather than at a user's runtime.

---

## Repository layout

```
src/
  Juggler.Core/          Rule engine, config, conditions, actions, journal.
                         IsAotCompatible=true. No UI, no Win32.
  Juggler.Daemon/        Process entry, tray, watcher, executor, reconciler.
                         net10.0, PublishAot=true. No UI framework, ever.
  Juggler.Ui/            WinForms rule editor. net10.0-windows. Never AOT.
tests/
  Juggler.Core.Tests/    Rule evaluation, config validation, journal, undo.
  Juggler.Daemon.Tests/  Budgets, backpressure, single-instance, focus rules.
tools/
  Juggler.Bench/         Measurement harness. Gates CI on memory budgets.
config/
  example.rules.json     Committed example. config/local.json is never tracked.
docs/                    This documentation set.
```

The dependency direction is strictly `Ui ? Daemon ? Core`, and `Core` depends
on nothing in the project. That is what lets the rule engine be tested without
spawning a process, and lets the daemon stay small enough to reason about.

---

## Reading order for a first contribution

1. `src/Juggler.Core/` - conditions and actions are plain functions over plain
   data. Start here; it is the easiest code to test.
2. [Safety and recovery](safety-and-recovery.md) before touching any action. If
   you add an action, it needs a journal record and a dry-run path.
3. `src/Juggler.Daemon/` - the pipeline above, then the P/Invoke.
4. The budget tests in `tests/Juggler.Daemon.Tests/`. They are the real
   specification; if your change breaks one, the change is wrong, not the test.
