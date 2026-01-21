---
description: Research on cross-platform process management patterns for client-server auto-launch scenarios
tags: [process, lifecycle, cross-platform, signals, locking]
audience: { human: 40, agent: 60 }
purpose: { research: 90, reference: 10 }
---

# Cross-Platform Process Management Research

Research for: Design decisions for a client that auto-launches a server process and needs to track its lifecycle.

*Research date: 2026-01-21*

## Context

A client application needs to:
1. Launch a server process (possibly detached)
2. Detect when the server dies
3. Prevent zombie processes
4. Coordinate with potentially multiple clients
5. Handle graceful and forced shutdown

This research covers platform differences between Windows, Linux, and macOS for each concern.

---

## 1. Process Launching

### .NET Process.Start Cross-Platform Behavior

| Aspect | Windows | Linux/macOS |
|--------|---------|-------------|
| Default `UseShellExecute` | `true` (.NET Framework), `false` (.NET Core+) | `false` |
| Shell execution mechanism | Windows Shell API | `xdg-open`, `gnome-open`, or `kfmclient` (Linux); `/usr/bin/open` (macOS) |
| Process resolution | Application directory, working directory, PATH | Similar to Windows |
| Environment inheritance | Automatic | Automatic |

> [Red Hat Developer - The .NET Process class on Linux](https://developers.redhat.com/blog/2019/10/29/the-net-process-class-on-linux) - documents Linux-specific behaviors

### Detaching Child Processes

**Windows**: Child processes are independent by default. When parent dies, children continue running.

**Linux**: Requires explicit detachment:
```bash
nohup /path/to/process &
```

In .NET, there is no built-in cross-platform API for detaching. Workarounds exist but lose the child PID.

> [dotnet/runtime Issue #104210](https://github.com/dotnet/runtime/issues/104210) - "Starting new Process as not child of the application" - documents the lack of cross-platform detach API

### Windowless Background Process

```csharp
var startInfo = new ProcessStartInfo {
    UseShellExecute = false,  // Required for CreateNoWindow to work
    CreateNoWindow = true,
    FileName = "server.exe"
};
```

If `UseShellExecute = true`, the `CreateNoWindow` setting is ignored.

> [Microsoft Learn - ProcessStartInfo.CreateNoWindow](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.createnowindow) - official documentation

### Failure Modes

| Failure | Platform | Cause |
|---------|----------|-------|
| Shell execute throws on Linux | Linux | `xdg-open` not installed |
| Arguments passed incorrectly | macOS | Bug passes args to `open` not target |
| No window suppression | Windows | `UseShellExecute = true` overrides |
| Lost PID on detach | Linux | Using shell with `nohup &` |

---

## 2. Death Detection

### .NET Process.Exited Event

| Aspect | Requirement |
|--------|-------------|
| Must set | `EnableRaisingEvents = true` before `Start()` |
| Thread safety | Event may fire on any thread |
| SynchronizingObject | Can marshal to UI thread |

**Known reliability issues:**

1. **Kill(entireProcessTree: true) on Windows**: Does not fire `Exited` event
   > [dotnet/runtime Issue #63328](https://github.com/dotnet/runtime/issues/63328)

2. **Shortcut files (.lnk)**: Process runs but event never fires

3. **GetProcessById race**: Setting `EnableRaisingEvents` after process exits throws `InvalidOperationException`
   > [dotnet/runtime Issue #1785](https://github.com/dotnet/runtime/issues/1785)

4. **Events may fire even when disabled**: Documentation notes `EnableRaisingEvents=false` offers no guarantee events won't fire
   > [dotnet/corefx Issue #14157](https://github.com/dotnet/corefx/issues/14157)

### Platform Differences in Exit Information

| Behavior | Windows | Linux |
|----------|---------|-------|
| Exit code retrieval | Works for any process with handle | Only valid for direct children |
| Process info after exit | Reference-counted, survives process | Single owner, reaped immediately |
| Runtime info properties | Available after exit | Throw `InvalidOperationException` |

> [Red Hat Developer](https://developers.redhat.com/blog/2019/10/29/the-net-process-class-on-linux) - "On Windows, you can retrieve StartTime, PrivilegedProcessorTime... after the process exited. On Linux, these properties throw InvalidOperationException."

### Alternative: Polling

```csharp
if (process.HasExited) { /* handle */ }
```

Polling is reliable but has latency. The `HasExited` accessor itself may raise `Exited` event as side effect.

### Alternative: WaitForExit

```csharp
process.WaitForExit();  // Blocking
process.WaitForExit(timeout);  // With timeout
```

Synchronous but guaranteed to detect exit.

---

## 3. Zombie Processes

### What Creates Zombies (Unix Only)

A process becomes a zombie when:
1. Child process exits
2. Parent has not called `wait()` to read exit status
3. Entry remains in process table

> [Wikipedia - Zombie process](https://en.wikipedia.org/wiki/Zombie_process) - "a process that has completed execution but still has an entry in the process table"

### Prevention Techniques

| Technique | How | Trade-off |
|-----------|-----|-----------|
| `wait()` / `waitpid()` | Parent reads exit status | Requires parent to actively wait |
| `SIGCHLD` handler | Handler calls `waitpid()` | Adds signal handling complexity |
| `signal(SIGCHLD, SIG_IGN)` | Kernel auto-reaps | Parent cannot get exit status |
| Orphan to init | Let child become orphan | Lose parent-child relationship |

> [GeeksforGeeks - Zombie Processes and Prevention](https://www.geeksforgeeks.org/operating-systems/zombie-processes-prevention/)

### .NET Behavior

.NET Core reaps child processes automatically as soon as they terminate. This means:
- No zombie accumulation
- But also: process info unavailable immediately after exit

### Container Considerations

In containers without an init process (PID 1), orphaned children are never reaped.

> Red Hat Developer - "If you are running in a container, often there is no init process. This means that no one is reaping orphaned children."

Solution: Use `docker run --init` or include an init like `tini`.

---

## 4. PID File Patterns

### Traditional Approach

1. Server writes its PID to a known file on startup
2. Client reads file to discover server PID
3. Client sends signal or checks `/proc/<pid>`

### Failure Modes

| Failure | Cause | Consequence |
|---------|-------|-------------|
| PID wraparound | PIDs reuse after ~32K-4M processes | Stale PID refers to wrong process |
| Race on delete | Old process deletes new process's PID file | Lost coordination |
| Race on create | Open and lock not atomic | Parallel starts corrupt file |
| Startup race | PID file created after daemon backgrounds | Brief window of missing file |

> [LWN.net - Toward race-free process signaling](https://lwn.net/Articles/773459/) - "PID reuse can happen quickly, and processes that work with PIDs might not notice immediately that a PID they hold referred to a process that has exited."

### PID Deletion Race Condition

> [Guido Flohr - Never Delete PID Files](https://www.guido-flohr.net/never-delete-your-pid-file/) - "The best way to delete a pid file (or rather a lock file) is not to delete it."

The race occurs because:
1. Process A writes PID file, exits
2. Process B starts, writes its PID
3. Process A's cleanup code (still running) deletes file
4. File now missing despite B running

### Solutions: Process File Descriptors (Linux 5.3+)

`pidfd_open()` creates a file descriptor referring to a process:
- FD remains valid reference even if PID reused
- `pidfd_send_signal()` guaranteed to signal correct process or fail
- Can poll/epoll the FD for process exit

> [man7.org - pidfd_open](https://man7.org/linux/man-pages/man2/pidfd_open.2.html)

**Limitation**: Linux-only, requires kernel 5.3+

---

## 5. File Locking

### Cross-Platform Differences

| Aspect | Windows | Unix/Linux | macOS |
|--------|---------|------------|-------|
| Lock type | Mandatory | Advisory | Advisory |
| API | `LockFileEx` | `fcntl` | `fcntl` |
| Enforcement | OS enforces | Cooperative processes only | Cooperative only |
| Atomic open+lock | Not supported | Not standard | Has `OPEN_EX`/`OPEN_SH` |

> [Wikipedia - File locking](https://en.wikipedia.org/wiki/File_locking) - "Unix-like operating systems do not normally automatically lock open files... file locks under Unix are by default advisory"

### .NET FileStream.Lock Behavior

```csharp
using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
fs.Lock(0, fs.Length);
```

| Platform | Behavior |
|----------|----------|
| Windows | Mandatory lock via `LockFileEx` |
| Linux | Advisory lock via `fcntl` |
| macOS | `[UnsupportedOSPlatform]` - throws |
| FreeBSD | `[UnsupportedOSPlatform]` - throws |

> [Microsoft Learn - Breaking change: FileStream locks files with shared lock on Unix](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/filestream-file-locks-unix)

### Best Practice: Separate Lock File

> "Always use special files for locking - if you want to restrict access to a certain file, do not place the lock on this file. Create a special file, e.g. by appending .lock"

### Named Mutexes Cross-Platform

| Platform | Implementation |
|----------|----------------|
| Windows | Kernel mutex object |
| Linux | pthread or file locks |
| macOS | File locks |

Requires `Global\` prefix on Linux for system-wide scope.

> [Medo64 - Single Instance Application for .NET 6 or 7](https://www.medo64.com/2022/12/single-instance-application-for-net-6-or-7/)

### Database Locks (SQLite Pattern)

SQLite demonstrates robust cross-platform locking:
- Uses `fcntl` advisory locks on Unix
- Uses `LockFileEx` on Windows
- WAL mode allows concurrent readers with single writer
- `busy_timeout` handles contention

> [SQLite - Write-Ahead Logging](https://sqlite.org/wal.html)

---

## 6. Signal Handling

### SIGTERM vs SIGKILL

| Signal | Can Catch | Can Block | Can Ignore | Cleanup Possible |
|--------|-----------|-----------|------------|------------------|
| SIGTERM | Yes | Yes | Yes | Yes |
| SIGKILL | No | No | No | No |
| SIGINT | Yes | Yes | Yes | Yes |

> [SUSE - SIGKILL vs SIGTERM](https://www.suse.com/c/observability-sigkill-vs-sigterm-a-developers-guide-to-process-termination/)

### .NET Signal Handling

**Modern approach (PosixSignalRegistration)**:
```csharp
using System.Runtime.InteropServices;

PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => {
    context.Cancel = true;  // Prevent default termination
    // Cleanup code here
});
```

> [Nelson Nobre - .NET Graceful Shutdown](https://nelsonbn.com/blog/dotnet-graceful-shutdown/)

**Generic Host**: Handles SIGTERM/SIGINT automatically via `IHostLifetime`.

### .NET 10 Breaking Change

Runtime no longer provides default SIGTERM handler. Applications using higher-level APIs (ASP.NET, Generic Host) are unaffected.

> [Microsoft Learn - .NET 10 SIGTERM change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/10.0/sigterm-signal-handler)

### Graceful Shutdown Timeout

Default: 30 seconds. Configurable via `HostOptions.ShutdownTimeout`.

```csharp
services.Configure<HostOptions>(options => {
    options.ShutdownTimeout = TimeSpan.FromSeconds(15);
});
```

### Ensuring Child Dies with Parent

**Windows - Job Objects**:
```
JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
```
Creates a "job" that terminates all member processes when last handle closes.

> [Meziantou - Killing all child processes when parent exits](https://www.meziantou.net/killing-all-child-processes-when-the-parent-exits-job-object.htm)

**Linux - PR_SET_PDEATHSIG**:
```c
prctl(PR_SET_PDEATHSIG, SIGTERM);
```

Child calls this to receive signal when parent thread dies.

> [man7.org - PR_SET_PDEATHSIG](https://man7.org/linux/man-pages/man2/pr_set_pdeathsig.2const.html)

**Caveats**:
- Signals on parent *thread* death, not process death
- Cleared on setuid/setgid exec
- SELinux/AppArmor may clear it on credential change

---

## Comparison: Coordination Mechanisms

| Mechanism | Windows | Linux | macOS | Race-Free | Survives Crash |
|-----------|---------|-------|-------|-----------|----------------|
| PID file | Yes | Yes | Yes | No | No |
| File lock | Mandatory | Advisory | Limited | Partial | Yes |
| Named mutex | Native | File-based | File-based | Yes | Yes |
| pidfd | No | Yes (5.3+) | No | Yes | N/A |
| Job object | Yes | No | No | Yes | Yes |
| Database lock | Yes | Yes | Yes | Yes | Yes |

---

## Gaps

What this research could not fully determine:

1. **Exact .NET Core behavior for reaping**: Documentation mentions automatic reaping but timing details unclear
2. **Named mutex implementation on BSDs**: Limited information
3. **Performance characteristics**: No benchmarks for lock contention scenarios
4. **Container runtimes other than Docker**: Podman, containerd behavior
5. **Network filesystem locking reliability**: Known to be problematic, specifics vary by NFS/SMB version

---

## Source Assessment

| Source Type | Examples | Confidence | Potential Bias |
|-------------|----------|------------|----------------|
| Microsoft Learn docs | API documentation | High | Vendor perspective |
| man7.org / man pages | System call documentation | High | Authoritative reference |
| dotnet/runtime issues | Real-world bug reports | High | Actual failures |
| Red Hat Developer | Linux .NET behavior | High | Linux-focused |
| Blog posts | Implementation patterns | Medium | Individual experience |
| Wikipedia | Conceptual explanations | Medium | General knowledge |

---

## References

### Process Launching
- [Red Hat Developer - The .NET Process class on Linux](https://developers.redhat.com/blog/2019/10/29/the-net-process-class-on-linux)
- [Microsoft Learn - ProcessStartInfo.UseShellExecute](https://learn.microsoft.com/en-us/dotnet/fundamentals/runtime-libraries/system-diagnostics-processstartinfo-useshellexecute)
- [dotnet/runtime Issue #104210](https://github.com/dotnet/runtime/issues/104210)

### Death Detection
- [Microsoft Learn - Process.Exited Event](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.exited)
- [dotnet/runtime Issue #63328](https://github.com/dotnet/runtime/issues/63328)
- [dotnet/runtime Issue #1785](https://github.com/dotnet/runtime/issues/1785)

### Zombie Processes
- [Wikipedia - Zombie process](https://en.wikipedia.org/wiki/Zombie_process)
- [GeeksforGeeks - Zombie Processes and Prevention](https://www.geeksforgeeks.org/operating-systems/zombie-processes-prevention/)
- [Baeldung - Zombie Processes in Operating Systems](https://www.baeldung.com/cs/process-lifecycle-zombie-state)

### PID Files and pidfds
- [LWN.net - Toward race-free process signaling](https://lwn.net/Articles/773459/)
- [LWN.net - Rethinking race-free process signaling](https://lwn.net/Articles/784831/)
- [man7.org - pidfd_open](https://man7.org/linux/man-pages/man2/pidfd_open.2.html)
- [Guido Flohr - Never Delete PID Files](https://www.guido-flohr.net/never-delete-your-pid-file/)

### File Locking
- [Wikipedia - File locking](https://en.wikipedia.org/wiki/File_locking)
- [Microsoft Learn - FileStream.Lock breaking change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/6.0/filestream-file-locks-unix)
- [SQLite - Write-Ahead Logging](https://sqlite.org/wal.html)
- [SQLite - File Locking and Concurrency](https://sqlite.org/lockingv3.html)

### Signal Handling
- [SUSE - SIGKILL vs SIGTERM](https://www.suse.com/c/observability-sigkill-vs-sigterm-a-developers-guide-to-process-termination/)
- [Nelson Nobre - .NET Graceful Shutdown](https://nelsonbn.com/blog/dotnet-graceful-shutdown/)
- [Microsoft Learn - .NET 10 SIGTERM breaking change](https://learn.microsoft.com/en-us/dotnet/core/compatibility/core-libraries/10.0/sigterm-signal-handler)
- [man7.org - PR_SET_PDEATHSIG](https://man7.org/linux/man-pages/man2/pr_set_pdeathsig.2const.html)

### Child Process Lifecycle
- [Meziantou - Killing all child processes when parent exits (Job Object)](https://www.meziantou.net/killing-all-child-processes-when-the-parent-exits-job-object.htm)
- [Old New Thing - Destroying all child processes](https://devblogs.microsoft.com/oldnewthing/20131209-00/?p=2433)
- [Medo64 - Single Instance Application for .NET 6 or 7](https://www.medo64.com/2022/12/single-instance-application-for-net-6-or-7/)
