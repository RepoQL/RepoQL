# Unix Domain Socket Failure Modes

Research for cross-platform socket handling decisions in RepoQL.

*Research date: 2026-01-21*

---
description: Cross-platform Unix domain socket failure modes, limitations, and mitigations
tags: [sockets, IPC, cross-platform, Windows, macOS, Linux, WSL]
audience: { human: 40, agent: 60 }
purpose: { research: 90, reference: 10 }
---

## Context

RepoQL requires reliable IPC across Windows, macOS, and Linux. Unix domain sockets are the cross-platform option available in .NET, but each platform has distinct failure modes, limitations, and edge cases.

---

## Windows

### Version Requirements

| Requirement | Value |
|------------|-------|
| Minimum build | Windows 10 Build 17063 (April 2018 Update) |
| Driver | `afunix.sys` |
| Header | `<afunix.h>` |

> [AF_UNIX comes to Windows](https://devblogs.microsoft.com/commandline/af_unix-comes-to-windows/) - Microsoft DevBlogs

### Detection

```bash
sc query afunix
```

Returns service info if available; fails otherwise.

### Supported vs Unsupported

| Feature | Status |
|---------|--------|
| SOCK_STREAM | Supported |
| SOCK_DGRAM | Not supported |
| SOCK_SEQPACKET | Not supported |
| Pathname sockets | Supported (UTF-8 paths) |
| Abstract sockets | Partially supported (no autobind) |
| socketpair() | Not supported |
| SCM_RIGHTS (fd passing) | Not supported |
| SCM_CREDENTIALS | Not supported |
| Ancillary data | Not supported |

> [AF_UNIX comes to Windows](https://devblogs.microsoft.com/commandline/af_unix-comes-to-windows/) - Microsoft DevBlogs

### Failure Modes

| Issue | Symptom | Detection | Mitigation |
|-------|---------|-----------|------------|
| Missing driver | Socket creation fails | `sc query afunix` returns not found | Fall back to named pipes or TCP loopback |
| Stale socket file | EADDRINUSE on bind | Attempt connect, check for ECONNREFUSED | Delete file before bind (file is NTFS reparse point) |
| Path with backslashes | Inconsistent behavior | N/A | Use forward slashes consistently |
| No SOCK_DGRAM | Socket type error | N/A | Use SOCK_STREAM only |

### Windows Alternative for FD Passing

Windows provides `WSADuplicateSocket()` for passing socket handles between processes, but this requires a different code path than Unix SCM_RIGHTS.

> [Know your SCM_RIGHTS](https://blog.cloudflare.com/know-your-scm_rights/) - Cloudflare Blog

---

## WSL

### Interop Architecture

Socket path determines communication partner:

| Socket Path Type | Can Communicate With |
|-----------------|---------------------|
| DrvFS (`/mnt/c/...`) | Windows AF_UNIX sockets only |
| LxFS (`/home/...`, `/tmp/...`) | WSL AF_UNIX sockets only |

A socket cannot communicate with both Windows and WSL processes.

> [Windows/WSL Interop with AF_UNIX](https://devblogs.microsoft.com/commandline/windowswsl-interop-with-af_unix/) - Microsoft DevBlogs

### Interop Requirements

First operation after socket creation must be `bind()` or `connect()`. Any other operation renders it WSL-only.

### WSL2 Regression

| Issue | Status |
|-------|--------|
| AF_UNIX on DrvFS paths | Broken in WSL2, worked in WSL1 |
| Error | `OSError: [Errno 95] Operation not supported` |
| Workaround | Use LxFS paths, or TCP/named pipes |

> [WSL Issue #5961](https://github.com/microsoft/WSL/issues/5961) - Microsoft GitHub (open since September 2020)

### DrvFS Issues

| Issue | Symptom | Mitigation |
|-------|---------|------------|
| Socket becomes regular file | Socket file type not preserved on DrvFS | Still functions, but confusing |
| Colon in filename | Creates NTFS Alternate Data Stream | Avoid colons in socket names |
| Metadata not enabled | Permission issues | Mount with `metadata` option in wsl.conf |
| No SCM_RIGHTS interop | Ancillary data stripped | Use alternative IPC for fd passing |

> [DrvFS socket issues](https://github.com/microsoft/WSL/issues/3643) - Microsoft GitHub

---

## macOS

### Path Length Limit

| Platform | sun_path limit |
|----------|---------------|
| macOS | 104 bytes |
| Linux | 108 bytes |

This is the `sun_path` field in `sockaddr_un`, not a filesystem path limit.

> [.NET Runtime Issue #79503](https://github.com/dotnet/runtime/issues/79503) - Microsoft GitHub

### Common Path Length Problem

```
/var/folders/7b/tkffkjy54676nvv44rpvv1l40000gn/T/  (49 chars)
```

Default temp directory already consumes ~49 characters, leaving ~55 for socket name.

| Issue | Symptom | Detection | Mitigation |
|-------|---------|-----------|------------|
| Path too long | `ArgumentOutOfRangeException` in .NET, `ENAMETOOLONG` in C | Check path length before bind | Use shorter paths, `/tmp`, or chdir workaround |

### Temp Directory Considerations

| Directory | Behavior |
|-----------|----------|
| `/tmp` | Cleaned on reboot; shorter path |
| `$TMPDIR` (`/var/folders/...`) | User-specific; cleaned after 3 days no access |
| `/var/folders/.../C/` (cache) | Not auto-cleaned |

Files in `/var/folders/.../T/` cleaned by `com.apple.periodic-daily` if not accessed for 3 days.

> [macOS temp files](https://til.codeinthehole.com/posts/how-temp-files-are-removed-on-macos/) - David Winterbottom

### App Sandbox Restrictions

| Scenario | Status |
|----------|--------|
| Same app group container | Allowed |
| Cross-team IPC | Blocked by sandbox |
| Temporary exception entitlement | Does not work for sockets |
| Non-sandboxed XPC service | Workaround |

> [Apple Developer Forums - Unix Domain Socket](https://developer.apple.com/forums/thread/756756)

### sandbox-exec Issues

`(deny network*)` blocks Unix sockets. Fix: add `(allow network* (remote unix))` after deny rule.

> [macOS sandbox-exec](https://jmmv.dev/2019/11/macos-sandbox-exec.html) - Julio Merino

### Path Length Workaround

Fork subprocess, `chdir()` to parent directory, bind socket with relative name (short), pass socket back via `socketpair()` + SCM_RIGHTS.

---

## Linux

### Abstract vs Filesystem Sockets

| Feature | Filesystem Socket | Abstract Socket |
|---------|------------------|-----------------|
| Path prefix | Normal path | Null byte (`\0`) |
| Persistence | Survives process death | Auto-removed on close |
| Permissions | Filesystem permissions apply | No permissions (anyone can connect) |
| Visibility | `ls`, `stat` | Only via `/proc/net/unix`, `ss`, `netstat` |
| Portability | All Unix-like systems | Linux only |

> [unix(7) man page](https://man7.org/linux/man-pages/man7/unix.7.html) - Linux manual

### Abstract Socket Security Issues

Abstract sockets ignore all permission checks:
- `umask(2)` has no effect
- `fchown(2)` and `fchmod(2)` have no effect on accessibility

| Vulnerability | Description |
|--------------|-------------|
| CVE-2022-42919 | Python multiprocessing forkserver allowed code injection via abstract socket |
| Container escape | Docker containers with network namespace sharing can access host abstract sockets |
| Chroot bypass | Abstract sockets bypass filesystem isolation |

> [Python CVE-2022-42919](https://github.com/python/cpython/issues/97514) - Python GitHub

**Mitigation**: Use filesystem sockets, or implement manual credential checking via `SO_PEERCRED`.

```c
getsockopt(fd, SOL_SOCKET, SO_PEERCRED, &cred, &len);
// Check cred.uid, cred.gid, cred.pid
```

### SELinux

SELinux can mediate Unix socket communication:
- `connectto` permission for stream connections
- `sendto` permission for datagram transmission
- Socket files get security context via extended attributes
- Abstract sockets have separate hooks

| Issue | Symptom | Detection | Mitigation |
|-------|---------|-----------|------------|
| Policy denial | Connection blocked | `ausearch -m avc` | Add policy rule or use `semanage` |
| Wrong context on socket file | Access denied | `ls -Z` shows context | Set correct type with `semanage fcontext` |

> [SELinux networking](https://namei.org/selinux-networking-lj.html)

### AppArmor

AppArmor uses path-based rules for Unix sockets:
- `addr=` conditional for socket address
- Abstract sockets addressed with `@` prefix
- Permissions: create, bind, listen, connect, send, receive, etc.

> [AppArmor man page](https://manpages.ubuntu.com/manpages/bionic/man5/apparmor.d.5.html) - Ubuntu

### Permission Issues

| Issue | Symptom | Detection | Mitigation |
|-------|---------|-----------|------------|
| No write permission on socket | EACCES on connect | Check socket file permissions | `chmod 666` or run as same user |
| No execute permission on directory | EACCES on bind/connect | Check parent directory permissions | `chmod +x` on directory |
| Socket in world-writable dir | Security concern | N/A | Use restricted directory (0700) |

---

## Cross-Platform Issues

### Stale Socket Detection

When a process dies without cleanup, the socket file remains.

| Detection Method | EADDRINUSE Response |
|-----------------|---------------------|
| Attempt `connect()` | If ECONNREFUSED, socket is stale (no listener) |
| | If ENOENT after unlink, file was already gone |
| | If connect succeeds, another process owns it |

> [Python asyncio Issue #425](https://github.com/python/asyncio/issues/425) - Python GitHub

### Cleanup Strategies

| Strategy | Pros | Cons |
|----------|------|------|
| Unlink before bind | Simple | May delete active socket |
| Connect probe first | Detects active listener | Extra syscall |
| Lock file | Atomic ownership | Additional file to manage |
| Abstract sockets (Linux) | Auto-cleanup | No permissions, Linux-only |
| PID file | Can verify process exists | Race conditions |

> [Mutagen socket overwrite modes](https://mutagen.io/documentation/forwarding/unix-domain-sockets/) - Mutagen docs

### Atomic Socket Creation

Sockets cannot use `O_EXCL` like files. Race conditions exist between checking for existing socket and binding.

| Issue | Vulnerability | Mitigation |
|-------|--------------|------------|
| Symlink attack | Attacker creates symlink before bind | Use secure temp directory (0700) |
| TOCTOU | Check existence, then bind | Use flock on parent directory, or accept race |

> [CVE-2000-0864](https://github.com/advisories/GHSA-6743-8vvq-693c) - GNOME esound socket race condition

### Error Code Reference

| Error | Meaning | Platform Notes |
|-------|---------|---------------|
| EADDRINUSE | Address in use | Socket file exists |
| ECONNREFUSED | No listener | Socket exists but no process bound |
| ENOENT | Path doesn't exist | Socket file or parent directory missing |
| EACCES | Permission denied | Filesystem permissions block access |
| ENAMETOOLONG | Path too long | macOS: 104, Linux: 108 |
| ENOTSUP / EOPNOTSUPP | Operation not supported | WSL2 DrvFS, unsupported socket type |

---

## Comparison Matrix

| Feature | Windows | WSL | macOS | Linux |
|---------|---------|-----|-------|-------|
| SOCK_STREAM | Yes (17063+) | Yes | Yes | Yes |
| SOCK_DGRAM | No | Yes | Yes | Yes |
| Abstract sockets | Partial (no autobind) | Yes | No | Yes |
| Path limit | N/A (UTF-8) | 108 | 104 | 108 |
| SCM_RIGHTS | No | No (interop) | Yes | Yes |
| SCM_CREDENTIALS | No | No (interop) | No | Yes |
| socketpair() | No | Yes | Yes | Yes |
| Auto-cleanup | No | Abstract only | No | Abstract only |
| Permission enforcement | NTFS ACLs | Mixed | Filesystem | Filesystem/SELinux/AppArmor |

---

## .NET Specific

### UnixDomainSocketEndPoint

- Available: .NET 5+
- Platforms: Windows 10 17063+, macOS, Linux

| Issue | Platform | Symptom | Mitigation |
|-------|----------|---------|------------|
| Path too long | macOS | `ArgumentOutOfRangeException` | Shorter paths |
| No SOCK_DGRAM | Windows | Type error | Use SOCK_STREAM |
| Backslash paths | Windows | Inconsistent | Use forward slashes |

> [Microsoft Learn - UnixDomainSocketEndPoint](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.unixdomainsocketendpoint)

---

## Gaps

- **Windows Server Core**: Whether afunix.sys is installed by default in minimal installations not verified
- **macOS SIP**: Specific SIP restrictions on socket locations not fully documented
- **Container runtimes**: Behavior of socket files across container boundaries varies by runtime
- **NFS/network filesystems**: Socket files on network mounts have undefined behavior

---

## Sources

- [AF_UNIX comes to Windows](https://devblogs.microsoft.com/commandline/af_unix-comes-to-windows/) - Microsoft DevBlogs
- [Windows/WSL Interop with AF_UNIX](https://devblogs.microsoft.com/commandline/windowswsl-interop-with-af_unix/) - Microsoft DevBlogs
- [unix(7) man page](https://man7.org/linux/man-pages/man7/unix.7.html) - Linux manual
- [.NET Runtime Issue #79503](https://github.com/dotnet/runtime/issues/79503) - macOS path length
- [WSL Issue #5961](https://github.com/microsoft/WSL/issues/5961) - WSL2 AF_UNIX regression
- [Python CVE-2022-42919](https://github.com/python/cpython/issues/97514) - Abstract socket vulnerability
- [junixsocket reference](https://kohlschutter.github.io/junixsocket/unixsockets.html) - Cross-platform reference
- [Mutagen socket docs](https://mutagen.io/documentation/forwarding/unix-domain-sockets/) - Cleanup strategies
- [Know your SCM_RIGHTS](https://blog.cloudflare.com/know-your-scm_rights/) - Cloudflare Blog
