# Performance

This document is the **performance specification**, not a summary of results. The
numbers below are budgets. The build fails if the code regresses past them.

Every claim here is reproducible with the harness described in
[Measurement](#measurement). If you cannot measure it, it does not belong in
this document.

---

## The budgets

### Steady state

| Scenario | Budget |
| --- | --- |
| Idle, tray visible, 10 watched roots, 0 events | ? 20 MB working set |
| Idle, 10,000 files already indexed | ? 24 MB working set |
| Rule evaluation, 1 file per second sustained | ? 32 MB working set |

The gap between the 10-root and 10,000-file rows is the point: the per-watched-file
cost must be **well under a kilobyte**. If watching a large tree is expensive,
the daemon is caching file lists, and that is a bug.

### Burst behaviour

| Scenario | Budget |
| --- | --- |
| 10,000 files copied into a watched root over 60 s | ? 80 MB peak |
| Recovery to idle baseline after the burst ends | ? 60 s |
| 100,000-file copy (extreme) | ? 150 MB peak, no unbounded growth |

**The mechanism that makes this hold** is backpressure, not cleverness. See
[Backpressure](#backpressure).

### Soak

| Duration | Requirement |
| --- | --- |
| 12 hours, mixed idle/churn | Working set **never** trends monotonically upward |

This is the only test that reliably finds leaks, because a leak smaller than a
single MB per hour is invisible in a 5-minute run. Sample working set every
30 s and fit a trend; a positive slope over the last 8 hours fails the build,
regardless of absolute numbers.

> A flat-ish line that sits 15 MB above baseline is still a pass for trend but a
> fail for the idle budget. Both tests are required.

### Hard invariants

| Invariant | Rationale |
| --- | --- |
| File contents are **never** read into memory | The only unbounded allocation we could never bound. v1 has no content conditions. |
| Log files are hard-capped: 5 MB ? 3 rotations | Uncapped logging is the most common silent memory growth in long-running services. |
| No unbounded queue between watcher and executor | See [Backpressure](#backpressure). |
| Native AOT, no CLR on the resident process | Removes JIT working set and GC heap growth entirely. |

---

## Why naive implementations use 150-400 MB

Worth understanding, because each of these is a mistake we are deliberately not
making.

**Unbounded event queue.** Copy 10,000 files in and the OS emits ~10,000 change
notifications. The natural implementation is `Queue<T>` + a worker. The queue
grows to hold every pending path, and the worker - slower than the producer
because it touches disk - never catches up. Memory scales with *burst size*,
not with steady-state rate. `ReadDirectoryChangesW` also reports a *batch* per
completion, so a single wakeup can carry thousands of entries.

**Managed `FileSystemWatcher` indirection.** `FileSystemWatcher` wraps the same
Win32 API but adds its own buffers, event-argument objects, and a thread per
watcher. Convenient, and one of the heaviest things you can put in a low-memory
process. We call `ReadDirectoryChangesW` directly.

**Reading files to decide.** Opening a PDF or a `.docx` to extract a date can
cost tens of megabytes per document, and libraries routinely hold the whole
document plus a decoded copy. If ten large documents arrive at once, that is a
hundred-plus megabytes of peak and a great deal of allocator churn. **v1 has no
content conditions.** If they are ever added, they must stream with a hard
buffer cap - see [Roadmap](roadmap.md#deferred).

**GC heap growth.** Managed runtimes let the heap grow because collecting is
expensive. A process that spikes once can keep the high-water mark. Native AOT
sidesteps this: there is no GC and no managed heap to grow.

**Timer-based polling.** Polling a folder tree every N seconds re-enumerates it
constantly. That is measurable disk activity and CPU on a laptop, which is the
"disturbing" half of the problem - see
[Background operation](background-operation.md).

---

## Backpressure

This is the single most important mechanism in the codebase.

```
  ReadDirectoryChangesW  ???  bounded Channel (cap 1024)  ???  executor
        64 KB buffer                drop-oldest on full           1-2 concurrent
```

When the channel is full, we do **not** block the watcher thread and we do
**not** grow the queue. We mark the root *dirty* and let the OS buffer the rest.
When the queue drains, a **reconciliation sweep** of that root picks up whatever
the OS coalesced or dropped.

The consequence: peak memory is bounded by `channel capacity ? max path length`,
which is a constant, regardless of whether 10 or 10 million files land in the
folder. The cost is that some events become a directory scan instead of a
targeted move - slower, but bounded and correct.

Reconciliation is also the safety net for events the OS drops on its own, which
happens on network shares and when buffers overflow. See
[Known limits](#known-limits).

---

## CPU budget

Memory is the headline, but a background tool that burns a core is just as
hostile.

| Scenario | Budget |
| --- | --- |
| Idle | < 0.1% of one logical core |
| Processing a file | < 5 ms CPU |
| Reconciliation sweep over 100,000 files | < 3 s wall clock, below-normal priority |

Enforcement:

- Process priority is `BELOW_NORMAL` at all times, dropping to `IDLE` when the
  tray is hidden and there is no pending work.
- Regex conditions are compiled once per rule at load and cached, never per
  file.
- **Evaluation is capped per tick.** The executor processes at most *N* files per
  scheduling slice and yields. A burst cannot monopolise the disk.
- Concurrent file operations are capped at 1-2. A large mechanical disk stalls
  badly under parallel random I/O, so parallelism here *hurts* throughput.

---

## Measurement

### Working set

```powershell
Get-Process juggler | Select-Object Id,
    @{N='WS_MB';E={[math]::Round($_.WorkingSet64/1MB,1)}}
```

`WorkingSet64` is the number the budget refers to. On Windows, resident memory
in Task Manager will read somewhat lower once the trimmer has run - budget
against `WorkingSet64` so the test is deterministic.

### The harness

The repo ships a harness so these numbers can be reproduced rather than
believed:

```powershell
# Steady-state: watch 10 roots, leave idle
dotnet run --project tools/Juggler.Bench -- steady --roots 10 --minutes 30

# Burst: drop N files and watch the peak
dotnet run --project tools/Juggler.Bench -- burst --files 10000 --seconds 60

# Soak: the leak test. Samples working set every 30 s and fits a trend.
dotnet run --project tools/Juggler.Bench -- soak --hours 12 --out soak-out/
```

All three exit non-zero when a budget is exceeded, which is how they gate CI.

### CI

`.github/workflows/ci.yml` runs `steady` and `burst` on every push - they take
under two minutes combined. The 12-hour soak runs nightly, because it is too
slow for a pull request but too important to run only when someone remembers.

A memory regression shows up as a diff on the reported numbers, so it is visible
in review rather than discovered on a user's machine.

---

## Known limits

Stated plainly, because a performance document that only lists wins is
marketing.

| Limit | Impact | Mitigation |
| --- | --- | --- |
| **Network shares** drop change notifications unpredictably | Rules may apply late | Reconciliation sweep is the safety net. Do not watch UNC paths for anything latency-sensitive. |
| **Reconciliation sweep cost** grows with tree size | Large trees scan slower | Sweep interval is configurable; default 15 min. Deep-tree traversal is depth-capped. |
| **Native AOT forbids reflection and `Assembly.Load`** | Some third-party libraries cannot be used | `Juggler.Core` sets `IsAotCompatible=true` and CI treats AOT warnings as errors. |
| **Content conditions are not implemented** | Rules cannot match on file contents | Deliberate. See [Roadmap](roadmap.md#deferred). |
| **Windows only** | No macOS or Linux | Native dependency on `ReadDirectoryChangesW` and the shell tray. By design for v1. |
