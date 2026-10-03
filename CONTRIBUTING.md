# Contributing

Thanks for looking at File Juggler. It is pre-alpha, so the design is more
settled than the code - which means this is a good time to influence both.

## Before you write code

**Open an issue for any new rule condition or action.** This is not gatekeeping;
it is the whole point of the project. A condition that opens a file to inspect
its contents can cost tens of megabytes, and the memory budget is the primary
feature. Some requests are genuinely good ideas that need a streaming design
instead of the obvious one, and it is much better to find that out in a comment
than in review.

For bug fixes, straight to a pull request is fine.

## The two things that must not regress

Most contributions are unremarkable. These two are load-bearing, and CI fails
the build if they break.

### 1. Memory budgets

From the [performance spec](docs/performance.md):

| Scenario | Budget |
| --- | --- |
| Idle, 10 watched roots | <= 20 MB working set |
| 10,000-file burst | <= 80 MB peak |
| 12-hour soak | Working set never trends upward |

If your change breaks a budget test, **the change is wrong, not the test.** The
usual cause is a new unbounded queue or a new per-file allocation that is not
bounded by a constant. Ask before working around it.

### 2. No focus theft

`SetForegroundWindow`, `SetActiveWindow`, and `BringWindowToTop` must never
appear in the resident process. A grep check in CI enforces this. The editor may
open windows - it is a separate process and it is what the user clicked.

Also: no new notifications without an explicit opt-in and a default of `off`.

## Dev setup

You will need:

- **.NET SDK 10** - `winget install Microsoft.DotNet.SDK.10`
- **Visual Studio 2022 Build Tools** with the *Desktop development with C++*
  workload - required for Native AOT publishing, **not** for ordinary builds:

  ```powershell
  winget install Microsoft.VisualStudio.2022.BuildTools --override `
    "--quiet --wait --norestart --includeRecommended --add Microsoft.VisualStudio.Workload.VCTools"
  ```

  This step needs administrator rights.

```powershell
git clone https://github.com/harshit5129/file-juggler.git
cd file-juggler
dotnet build
dotnet test
```

`docs/` holds design rationale and the performance budget. It is published, and
the README and this file are written to stand alone without it.

## Code layout

Dependency direction is strictly `Ui -> Daemon -> Core`. `Core` depends on nothing
in the project.

- **`Juggler.Core`** - rule engine, config, conditions, actions, journal. Plain
  data in, plain results out. No UI, no Win32. Easiest place to start and to
  test.
- **`Juggler.Daemon`** - entry point, tray, watcher, executor. No UI framework,
  ever. `PublishAot=true`, so no reflection, no `Assembly.Load`, no
  `Reflection.Emit`.
- **`Juggler.Ui`** - WinForms editor. Never AOT, never resident.

If you add an action, it needs all four of: a journal record written before
execution, a dry-run path, a `Juggler.Core.Tests` case, and a rule-reference
entry.

## Pull requests

- One logical change per PR
- Tests for new behaviour, especially budget regressions
- Update `CHANGELOG.md` under `[Unreleased]`
- Conventional commit prefixes: `feat:`, `fix:`, `docs:`, `perf:`, `test:`,
  `build:`, `chore:`, `refactor:`

## Code of conduct

Be straightforward and assume good faith. Review the technical work, not the
person. See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
