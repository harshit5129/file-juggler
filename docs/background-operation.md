# Background operation

"How do I make a program that runs all day and never bothers me?"

This document is the answer, as a specification rather than a list of
suggestions. Each rule below is testable, and several are enforced by tests or
by the compiler.

The guiding rule: **the app must never be the reason a user notices their
computer.** Not with a window, not with a notification, not with a fan spinning
up, not with a battery that drained overnight.

---

## The contract

### 1. It is never in the taskbar

The tray icon is the only persistent UI surface.

- The message-only window is created with `WS_EX_TOOLWINDOW` so it has no
  taskbar button.
- The rule editor is a **separate process**. It appears in the taskbar only
  while you are actually looking at it.

Consequence: at no point does a File Juggler window exist unless you opened it.

### 2. It never takes focus

`SetForegroundWindow` is **never called**. Not once, not in any code path. The
editor window opens only in response to an explicit click on the tray menu.

Windows has a long history of well-meaning apps stealing focus. If you are
reviewing a pull request, any call to `SetForegroundWindow`, `BringWindowToTop`,
`ShowWindow(..., SW_SHOW)`, or `SetActiveWindow` in the resident process is an
immediate rejection - enforced as a grep check in CI.

Double-clicking the tray icon is treated as a *user request* to show a window and
is the only path that does so.

### 3. It never raises a notification unless asked

Tray balloon notifications are **opt-in and default off** (`notify: false`).

A tool that sorts files all day has nothing interesting to say. Every routine
action goes to the log. The only events that may ever raise a notification are
ones the user explicitly enables:

| Event | Default | Rationale |
| --- | --- | --- |
| Rule applied | off | Fires constantly; pure noise. |
| Files moved / renamed | off | Same. |
| Rule failed | off | Logged instead; see below. |
| Watched folder disappeared | **on** | The user's intent can no longer be met - worth knowing. |
| Process entered low-memory safety mode | **on** | The app is degrading; the user should know why files are not being sorted. |

Errors never surface as a dialog. A rule that fails 400 times must not produce
400 interruptions. Failures set the tray icon to a warning state, are written to
the log, and are counted in `juggler doctor`.

### 4. It yields to the user

Background does not mean competing.

**Process priority.** `BELOW_NORMAL_PRIORITY_CLASS` for the lifetime of the
process, dropping to `IDLE_PRIORITY_CLASS` when the tray is hidden and no work is
pending. Foreground applications win, always.

**Per-tick work cap.** The executor processes at most *N* files per scheduling
slice, then yields. A burst of 10,000 files cannot monopolise the disk.

**Single worker.** One file operation at a time by default (`maxConcurrent: 1`).
Parallel random I/O on a mechanical disk *reduces* throughput and increases
seek thrash; parallelism here actively hurts.

### 5. It sleeps when you are not there

Running while you are away is the whole point - but also when it can yield.

| Condition | Behaviour | Restore |
| --- | --- | --- |
| Session locked | Suspend file actions | On unlock |
| On battery power | Throttle to 1 file per 5 s | On AC power |
| Monitor off | Throttle to 1 file per 30 s | On display on |
| Screen saver active | Suspend | On dismiss |
| Idle > 30 min | Suspend | On user activity |

Detection uses `WTSRegisterSessionNotification` for lock/unlock,
`RegisterPowerSettingNotification` for AC/battery, and
`WM_POWERBROADCAST` for monitor state. No polling timers.

Suspending means: stop starting new work, finish or abandon the current file
cleanly, keep the watcher registered. It does **not** mean exiting - the process
resumes instantly on the next event.

> **Never move files during a screen share or a full-screen game.** This is a
> known gap: we cannot detect either reliably. If it matters for your setup, use
> scheduled mode (?7), which only runs at times you choose.

### 6. It starts and stays started, quietly

**Autostart** registers a per-user `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
entry. Per-user specifically: it needs no administrator rights, it does not
touch machine-wide state, and it is removed cleanly on uninstall.

Windows may also show a *"started by an app"* or *"launch app*" prompt depending
on policy. That is a Windows behaviour, not ours, and there is no supported way
to suppress it for a per-user Run entry. If your environment forbids it, use
scheduled mode.

### 7. Scheduled mode: zero resident process

```powershell
juggler.exe --once
```

Runs the full rule set, applies everything pending, exits. All memory is
returned to the OS immediately. Register with Task Scheduler to get genuinely
zero resident footprint.

| | Resident | Scheduled |
| --- | --- | --- |
| Idle memory | 8-20 MB | **0 MB** |
| Reaction time | milliseconds | up to interval |
| Misses files created while off | No - watcher is running | Yes - caught by sweep at next run |
| CPU while doing nothing | ~0 | None at all |

Scheduled mode is the right answer if you want zero footprint and can accept
latency. It is also the right answer for the screen-share case above.

Both modes share the same executable and the same rule engine. The only
difference is whether the process exits at the end of a pass.

### 8. Exactly one instance

Two instances would double the memory **and** every rule would run twice - the
second instance would find files the first already moved, producing duplicate
work and confusing logs.

A named mutex (`Local\FileJuggler.Daemon.{user}`) is acquired at startup. A
second launch detects the mutex, signals the running instance to show its tray
menu, and exits cleanly. No error dialog, ever.

The mutex is per-user so two people can each run their own instance with their
own rules on the same machine.

---

## Autostart and uninstall

| Action | What happens |
| --- | --- |
| First run | Asks once whether to register autostart. Default **yes**. One question, then never again. |
| `juggler.exe --install-autostart` | Registers without asking |
| `juggler.exe --uninstall-autostart` | Removes the Run entry |
| Uninstall | Removes the Run entry, config directory, and journal. **Never** touches user files it moved - that is what the journal is for. |

`juggler.exe doctor` prints whether autostart is registered, whether a stale
mutex exists, current priority class, and the last few warnings. It is the first
thing to ask for in a bug report.

---

## Verifying it behaves

Behaviour in this document is checkable:

```powershell
# No taskbar button, correct priority, single instance
dotnet run --project tools/Juggler.Bench -- behaviour
```

That check asserts, and fails on violation of:

- `SetForegroundWindow` / `SetActiveWindow` / `BringWindowToTop` are absent
  from the resident binary
- priority class is `BELOW_NORMAL` or `IDLE`
- no top-level window is visible while the tray is idle
- a second launch exits without creating a second process
- the tray icon has no taskbar presence (`WS_EX_TOOLWINDOW` set)
- notification defaults are off in a fresh config

CI runs it on every push.

---

## Troubleshooting

| Symptom | Cause | Fix |
| --- | --- | --- |
| Tray icon missing | Not running, or Explorer crashed and never restored the icon | Check Task Manager for `juggler.exe`. Restart Explorer if it was already running. |
| Files are not being sorted | On battery / locked / screen saver - suspended by design | `juggler.exe doctor` shows current state |
| Rules apply minutes late | Event loss; reconciliation sweep has not run yet | Lower `sweep.intervalMinutes`. Check whether the path is a network share - see [Known limits](performance.md#known-limits). |
| Two tray icons | Two instances; mutex failed (second user session, or corrupted mutex) | `juggler.exe doctor` reports stale mutexes |
| Aggressive disk use | `maxConcurrent` raised, or `sweep.intervalMinutes` too low | Restore defaults |
| Started a full-screen game and files moved anyway | Cannot detect; documented gap | Use scheduled mode |
