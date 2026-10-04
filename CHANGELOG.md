# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Nothing yet.

## [0.1.1] - Unreleased

Bug-fix release. The 0.1.0 editor **crashed when adding a rule**, so upgrade before
relying on it.

### Fixed

- **Adding a rule crashed the app.** Two independent causes:
  1. `RuleEditorWindow` and `DiagnosticsWindow` resolved their controls in
     `OnInitialized`. For a `Window`, Avalonia raises that from inside the *base*
     constructor, which in C# runs before the derived constructor body, so
     `InitializeComponent()` had not yet run and the logical tree was empty. Every
     lookup threw `No TextBlock named 'HeaderTitle' found`. Moved to `OnOpened`,
     which is the first point at which the XAML is guaranteed loaded.
  2. `RulesView.ApplyFilter` replaced `ItemsControl.ItemsSource` wholesale on every
     refresh. Adding a rule reassigns it, which tore down every item container
     mid-frame and Avalonia's `ContentPresenter` null-referenced. The list now binds
     its source once and filters by toggling row visibility. `RuleRow` also honours
     the in-place design its own documentation described, and raises change
     notifications, so the enabled toggle no longer loses state across a reload.
- **Subfolder recursion defaulted to on.** A rule pointed at a folder silently
  claimed files nested arbitrarily deep beneath it. Now opt-in per rule, with a
  clearer label and a hint saying what the toggle actually does.
- **Autostart always reported as unregistered.** The installer writes the Run value
  as `"File Juggler"` while the diagnostics report queried `"FileJuggler"`. The
  mismatch was silent, so a user could not tell whether autostart was working.
  Locked by a contract test that reads the installer script.
- Registry access in the diagnostics report is guarded by
  `OperatingSystem.IsWindows()` rather than relying on a caught exception.

### Changed

- The configuration serializer now uses the source-generated `AppConfigJsonContext`.
  That context already existed but was dead code: `ConfigStore` still called the
  reflection-based overloads, which are `RequiresUnreferencedCode` and
  `RequiresDynamicCode` and cannot survive NativeAOT. Its `UseStringEnumConverter`
  was never enabled, which is very likely how enums came to be written as integers
  in the first place. `GenerationMode` moved from `Metadata` to `Default`, because
  `Metadata` emits type metadata only and cannot serialize.
- Build warnings reduced from 10 to 2. The remaining two are Avalonia noting that the
  two windows have no public parameterless constructor, which is expected for windows
  that take constructor arguments.
- Added `tools/uismoke.ps1`, which drives the real UI through UI Automation and fails
  on a crash or a fresh stack trace. Both crashes above were invisible to the build
  and to the existing tests; they only appeared once the app was actually clicked.

### Known issues

- **Right-hand controls can clip.** On a 125% display the window frame is created
  smaller than Avalonia lays out, so trailing controls in the header, toolbars and
  rule rows render past the window edge. DPI awareness is verified correct
  (per-monitor v2, manifest embedded in both configurations), which rules out the
  obvious cause. Explicitly resizing the window makes it render correctly, so the
  frame does respond to size changes; only the initial size is wrong. Unresolved.
## [0.1.0] - 2026-10-04

First public release. Pre-alpha: the engine, config layer and rule editor work
and are tested, but the resident daemon does not exist yet, so **no rule has ever
been applied to a file on disk.**

### Added

- Rule engine: monitor / if / then model with extension, glob, regex, size and
  date conditions, and move, copy, rename, sort-into-folders, Recycle Bin and
  allowlisted-command actions
- Configuration validator with per-rule diagnostics and a draft mode for
  partially-specified rules
- Config store: atomic temp-and-rename writes, full validation before persisting,
  hot reload through a file watcher, and an undo-journal path
- `config/example.rules.json`, covered by a test so it cannot drift from the
  schema the code accepts
- Avalonia rule editor: rules list, rule editor window, activity log, settings
  and a diagnostics report, as an on-demand process that holds no memory at idle
- Brand emblem wired in as a multi-resolution `Icon.ico` and a 512 px `Icon.png`
- 22 tests covering the validator, the config store round trip and the
  safety guards described below

### Fixed

- **The editor could destroy the user's rules.** Two independent bugs both
  overwrote a real config with `"rules": []`:
  1. The serializer wrote enums as integers, while the shipped example config
     used names. Deserializing `"mode": "Resident"` threw, the load was reported
     as failed, the in-memory config stayed at its empty default, and the next
     settings save wrote that default back over the user's rules.
  2. An atomic save briefly makes the config path unresolvable. The watcher fired
     inside that window, `Load()` reported a *missing* file as empty defaults
     with no error, and those defaults were adopted and persisted.
  Both paths are now covered by regression tests. `Save()` refuses to write when
  the last load failed, and only a genuinely parsed file is treated as
  authoritative.
- The file watcher used a leading-edge throttle, so its *first* event landed
  inside exactly the window it needed to avoid. Replaced with a trailing-edge
  debounce that polls until the rename settles.
- Theme resources resolved against the wrong palette on startup, because
  `RequestedThemeVariant` was set in `Window.Opened` rather than before the first
  window was realized. A dark window rendered one control in the light palette.
- Mojibake in the rules search placeholder, and throughout several source files.

### Changed

- Adopted Avalonia 12 for the editor. WinForms and WPF cannot be trimmed under
  NativeAOT, so a UI framework is unacceptable in the resident process; the
  editor is a separate on-demand process where that trade-off costs nothing.
- Declared PerMonitorV2 DPI awareness in the application manifest and set it
  before Avalonia initializes.
- Reworked the layout so right-docked control clusters sit in a trailing `Auto`
  column instead of behind a star spacer, which was pushing them off-screen.
- Retuned the design tokens to a single-accent, hairline-ruled palette with a
  three-face type hierarchy (Bahnschrift SemiBold display, Inter body, Cascadia
  Mono for paths and counters).

### Known issues

- On a 125% DPI display the window frame is created smaller than Avalonia lays
  out, so trailing controls in the header, toolbars and rule rows can render past
  the window edge.

[Unreleased]: https://github.com/harshit5129/file-juggler/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/harshit5129/file-juggler/releases/tag/v0.1.0