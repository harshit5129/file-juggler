# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Nothing yet.

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