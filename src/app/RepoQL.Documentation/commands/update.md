---
description: "repoql update — check for and install the latest RepoQL version"
tags: ["command", "update", "upgrade", "version", "install"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# Update Command

Check for and install the latest RepoQL version.

**Update:** `repoql update`
**Check only:** `repoql update --check`
**Force reinstall:** `repoql update --force`

---

## Capsule: BasicUsage

**Invariant**
`repoql update` compares the current version against `https://downloads.repoql.ai/latest/version.txt` and downloads the new binary if one is available.

**Example**
```
repoql update
  Current version: 1.5.3
  Latest version:  1.5.4
  Downloading ████████████████████ 100% 12.5 MB/s 00:00
  Updated to 1.5.4.
```

**Depth**
- Downloads the platform-appropriate binary (win-x64, win-arm64, osx-arm64, osx-x64, linux-x64, linux-arm64)
- Replaces the running binary via rename-swap (current -> .old, new -> current)
- Sets executable permission on Unix platforms
- Warns if a RepoQL host is running and needs restart
- Falls back to manual install instructions if the binary path cannot be determined

---

## Capsule: CheckOnly

**Invariant**
`repoql update --check` reports whether an update is available without changing anything.

**Example**
```
repoql update --check
  Current version: 1.5.3
  Latest version:  1.5.4
  A new version is available. Run 'repoql update' to install it.
```

---

## Capsule: ForceReinstall

**Invariant**
`repoql update --force` downloads and replaces the binary even when the version matches. Use this to recover from a corrupted installation.

**Example**
```
repoql update --force
  Current version: 1.5.3
  Latest version:  1.5.3
  Downloading ████████████████████ 100% 12.5 MB/s 00:00
  Reinstalled 1.5.3.
```

---

## Capsule: AlreadyCurrent

**Invariant**
When the installed version matches or exceeds the latest, no download occurs.

**Example**
```
repoql update
  Current version: 1.5.3
  Latest version:  1.5.3
  RepoQL is up to date.
```

---

## Capsule: ErrorRecovery

**Invariant**
When the update fails, the command tells you exactly how to recover.

**Depth**
- Download failures: current installation unchanged, reports the HTTP error
- Permission denied: suggests running as Administrator (Windows) or sudo (Unix)
- Replacement failures: tells you to rename `repoql.exe.old` back, or reinstall from scratch
- Running host detected: warns you to restart after a successful update

---

## Help

```bash
repoql update --help
```
