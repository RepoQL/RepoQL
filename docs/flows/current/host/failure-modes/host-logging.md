# Host Logging

Persistent file-based logging for host diagnostics.

## Problem

Currently, host stderr is only captured by the process that launched it. This creates diagnostic gaps:

```
Scenario: Crash and reconnect

1. MCP server launches host A
2. Host A crashes (OOM, unhandled exception)
3. MCP server reconnects, launches host B
4. Host B is healthy
5. User runs :diagnostics:
6. Diagnostics: "✓ All OK" (host B is fine)
7. User: "But I just saw errors!"
8. Stderr from host A: gone forever
```

The agent gets misled because the evidence of what went wrong is lost.

## Solution

Log to a file in `.repoql/` that persists across host restarts.

```
.repoql/
├── host.log              # Log file (1MB max)
└── index.duckdb
```

## Requirements

### Size Limit

```
Max file size:    1 MB
```

1MB is plenty for diagnostic purposes. File sink handles truncation/rotation.

### Log Format

Standard .NET logging format. Key events to log:

```
[10:23:45 INF] Host starting, version 1.2.3, PID 12345
[10:23:45 INF] Preflight complete, repo=/home/user/myproject
[10:23:45 INF] Socket bound at .repoql/repoql.sock
[10:23:46 INF] Database opened, 125 MB
[10:23:46 WRN] Embeddings fallback to hashed (ONNX init failed)
[10:23:47 INF] Indexer started, 1247 files
[10:23:48 INF] Health SERVING
[10:30:00 ERR] OutOfMemoryException
[10:30:00 ERR]   at BatchProcessor.ProcessBatch()
```

## Implementation

Use standard .NET logging with a file sink:

```csharp
var logPath = Path.Combine(repoRoot, ".repoql", "host.log");
builder.Logging.AddFile(logPath, options =>
{
    options.FileSizeLimitBytes = 1_000_000;  // 1MB
});
```

Register `AppDomain.UnhandledException` to log crashes before exit.

## Diagnostic Integration

When diagnostics run, read last 20 lines of the log:

```
Recent log lines:
  [10:29:58 INF] Processing batch 45/50
  [10:29:59 WRN] Memory pressure, triggering GC
  [10:30:00 ERR] OutOfMemoryException
  [10:30:00 ERR]   at BatchProcessor.ProcessBatch()

Current Host (PID 12389)
========================
Started: 10:30:15 (2m 30s ago)
Status: SERVING
```

If the log ends with an ERROR and the host isn't running, we know it crashed.

## Status

❌ **Not implemented** - Host only logs to stderr.

**Implementation**:
1. Add file sink to host logging configuration
2. Register unhandled exception handler to log before exit
3. Read log tail in diagnostics
