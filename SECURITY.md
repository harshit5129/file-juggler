# Security Policy

## Supported versions

The project is pre-alpha and unreleased. There are no supported versions yet,
and no released version has received a security audit.

## Reporting a vulnerability

Please report security issues privately rather than opening a public issue.

Use **GitHub's private vulnerability reporting** on this repository
(Security -> Report a vulnerability). If that is unavailable, open a public issue
containing **only** the general nature of the problem and no exploit details,
then wait for a maintainer to reach out.

Please do not open a public issue containing working exploit code.

Include, where you can:

- What the issue is and its impact
- Steps to reproduce, ideally on a throwaway VM
- The version or commit hash
- Your OS build

Expect an acknowledgement within a few days. Fixes land on `main` first and are
disclosed publicly after release.

## Threat model

This is a local, single-user desktop tool. It is not a network service, and it
does not listen on any port. That shapes what actually matters.

### What the tool legitimately does

File Juggler **moves, renames, copies and deletes files using your own Windows
permissions**, automatically, while running in the background. That is the
product. It has no sandbox and no confinement - by design, because a file
organizer that cannot reach your files would be useless.

The consequence: **a malicious or careless rule is destructive.** Rules are
configured by you and stored in `config/`. Anything that can write to that file
can make the daemon move or delete your data on the next event.

### Realistic risks

| Risk | Status |
| --- | --- |
| A malformed or hostile `config/` causing destructive unintended actions | Mitigated by full validation before reload, dry-run, undo journal, Recycle Bin deletes |
| Symlink / junction traversal causing files outside the monitored tree to be touched | Reparse points are filtered; being hardened further |
| Path traversal via rule fields (`..` segments, absolute paths in `into`) | Being hardened - see below |
| TOCTOU between a condition evaluating and the action executing | Being hardened |
| Unbounded resource use from a crafted config | Being hardened |
| `Run command` action executing attacker-controlled arguments | Being hardened - commands must come from a named allowlist, not raw config |
| Log files exposing filesystem layout | Logs contain paths and are local-only; `config/` is excluded from version control |

### Sensitive data

- **`config/` contains real filesystem paths** - usernames, folder structure,
  sometimes enough to map a disk. It is git-ignored. Do not paste it into a
  public issue. `juggler.exe doctor` output is designed to be safe to share;
  prefer that.
- **The undo journal** records every move with full source and destination paths,
  and is a complete history of what the tool did to your disk. Treat it as
  sensitive. It lives in your local app data directory and is never uploaded.
- **Logs** contain paths and error text, same handling as the journal.

### No telemetry

File Juggler has no telemetry, no analytics, and no network calls of any kind.
It cannot, by design. If you ever see it attempting a network connection, that is
a serious bug - please report it.

## Running untrusted configs

The project publishes no binaries yet. When it does:

- Verify release checksums published alongside the release
- Release CI signs artifacts; prefer signed releases
- Review `config/` before loading it, exactly as you would a shell script

## Out of scope

- Running File Juggler on a machine where you are not the administrator and
  expect filesystem isolation. There is none.
- Compensating for a deliberately malicious `Run command` rule. That feature is
  being hardened to an allowlist, but a local administrator can always run code
  directly.
