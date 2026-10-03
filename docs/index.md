# Documentation

File Juggler watches folders in the background and applies rules to the files
that appear in them. This is the complete documentation set.

## Start here

| If you want to | Read |
| --- | --- |
| Get a first rule working | [Getting started](getting-started.md) |
| Know what a rule can do | [Rule reference](rule-reference.md) |
| Change a setting | [Configuration](configuration.md) |
| Understand how it works | [Architecture](architecture.md) |

## Reference

| Document | Contents |
| --- | --- |
| [Rule reference](rule-reference.md) | Every `If` condition and `Then` action, matched against semantics, with examples |
| [Configuration](configuration.md) | The config file schema, every key, and its default |

## How it behaves

These two documents are **specifications**, not guides. The numbers and rules in
them are what the test suite enforces.

| Document | Contents |
| --- | --- |
| [Performance](performance.md) | Memory budgets, backpressure, CPU budget, how to measure, known limits |
| [Background operation](background-operation.md) | Tray-only behaviour, focus rules, notifications, priority, autostart, scheduled mode |

## Operating it

| Document | Contents |
| --- | --- |
| [Safety and recovery](safety-and-recovery.md) | Dry-run, undo journal, Recycle Bin, recovering from a bad rule |

## Building it

| Document | Contents |
| --- | --- |
| [Development](development.md) | Prerequisites, build, native AOT publish, tests, debugging |
| [Roadmap](roadmap.md) | What exists, what does not, what is planned |
| [Architecture decisions](adr/) | Why it is built this way, and what was rejected |

## Command reference

| Command | Purpose |
| --- | --- |
| `juggler.exe` | Run in resident tray mode |
| `juggler.exe --once` | Run one pass and exit (for Task Scheduler) |
| `juggler.exe --dry-run` | Report what would happen; change nothing |
| `juggler.exe doctor` | Print diagnostic state - first thing to attach to a bug report |
| `juggler.exe --config <path>` | Use an alternative config file |
| `juggler.exe --install-autostart` / `--uninstall-autostart` | Manage the per-user Run entry |
| `Juggler.Ui.exe` | Rule editor; launches on demand, exits when closed |
