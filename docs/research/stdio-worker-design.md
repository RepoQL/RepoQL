---
description: Research on designing out-of-process C# workers communicating over stdio — framing, lifecycle, concurrency, serialization, diagnostics.
tags: [research, ipc, stdio, worker, process, c-sharp]
audience: { human: 50, agent: 50 }
purpose: { research: 90, design: 10 }
---

# Out-of-Process C# Workers over Stdio

Research for designing out-of-process workers that communicate with a host process via stdin/stdout.

*Research date: March 18, 2026*

## Context

RepoQL's architecture includes components that benefit from process isolation — parse workers, embedding generation, format handlers. The host process (gRPC server) needs to dispatch work to child processes over stdio, with reliable framing, graceful lifecycle management, and diagnostic visibility.

This research surveys the landscape: how .NET handles stdio, what framing protocols exist, how production C# tools solve these problems, and what pitfalls to avoid. It informs a design document for RepoQL's parse worker architecture.

**In scope:** stdio IPC patterns in C#/.NET, framing protocols, process lifecycle, concurrency, serialization, diagnostics, and testing.
**Out of scope:** Named pipes, Unix domain sockets, and TCP as primary transports (covered briefly for comparison). gRPC-specific patterns (RepoQL already uses gRPC for host↔client communication).

---

## .NET Stdio Fundamentals

### Stream Hierarchy

.NET provides two layers for stdio access:

| API | Returns | Encoding | Thread-safe | AutoFlush |
|-----|---------|----------|-------------|-----------|
| `Console.OpenStandardInput()` / `Output()` | Raw `Stream` (byte-level) | None — raw bytes | No | N/A |
| `Console.In` / `Console.Out` | `TextReader` / `TextWriter` | `Console.InputEncoding` / `OutputEncoding` | Yes (via `SyncTextWriter`) | Yes |

`Console.Out` wraps a `StreamWriter` with `AutoFlush = true`, flushed after every `Write()` call. The `SyncTextWriter` wrapper serializes all writes under a lock.

> [dotnet/runtime Console.cs](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Console/src/System/Console.cs) — source
> [Microsoft Learn: StreamWriter.AutoFlush](https://learn.microsoft.com/en-us/dotnet/api/system.io.streamwriter.autoflush) — behavior

For byte-level protocols, use `Console.OpenStandardInput()`/`OpenStandardOutput()` directly. Wrapping in a custom `StreamReader` with a larger buffer size (e.g., 8192+) significantly improves throughput — one measurement showed ~105ms vs ~490ms for large inputs.

> [Fast Console IO on .NET](https://medium.com/@epeshk/fast-console-io-on-net-6cb56a6db529) — benchmarks

### Encoding

| Runtime | `Console.OutputEncoding` default | `Encoding.Default` |
|---------|----------------------------------|-------------------|
| .NET Framework | System OEM code page (e.g., IBM437) | System ANSI code page |
| .NET Core / .NET 5+ | UTF-8 | UTF-8 |

> [dotnet/runtime #17849](https://github.com/dotnet/runtime/issues/17849) — encoding defaults

**The BOM trap:** `Encoding.UTF8` (static property) emits a BOM (`0xEF 0xBB 0xBF`). For binary protocols over stdio, a BOM at stream start corrupts the protocol. Use `new UTF8Encoding(false)` for BOM-free UTF-8. The BOM is written by `StreamWriter` on first write if `GetPreamble()` returns non-empty bytes.

> [Microsoft Learn: Encoding.UTF8](https://learn.microsoft.com/en-us/dotnet/api/system.text.encoding.utf8) — BOM behavior
> [dotnet/runtime #51353](https://github.com/dotnet/runtime/issues/51353) — BOM discussion

### Stdin Cancellation Problem

`Console.OpenStandardInput()` returns a stream whose `ReadAsync` does **not** respect `CancellationToken` on Windows or Unix. Neither `WindowsConsoleStream` nor `UnixConsoleStream` cancels I/O on Dispose. The MCP C# SDK works around this with a `CancellableStdinStream` wrapper that calls `.WaitAsync(cancellationToken)` on the read task. Other projects dispose the stream handle to force-unblock reads.

> [dotnet/runtime #100308](https://github.com/dotnet/runtime/issues/100308) — async console read cannot be cancelled
> [dotnet/runtime #77358](https://github.com/dotnet/runtime/issues/77358) — `StreamReader.ReadLineAsync` blocks on empty stdin

### OS Pipe Buffer Sizes

| Platform | Default pipe buffer | Atomic write guarantee (`PIPE_BUF`) | Configurable? |
|----------|--------------------|------------------------------------|---------------|
| Linux | 65,536 bytes (16 × 4096-byte pages) | 4,096 bytes | Yes, `fcntl(F_SETPIPE_SZ)` up to `/proc/sys/fs/pipe-max-size` |
| macOS | 16,384 bytes (grows to 65,536 on large writes) | 512 bytes | No |
| Windows | Advisory via `CreatePipe(nSize)`. ~4,096 when `nSize=0` | N/A | Via `nSize` parameter |

When a pipe buffer fills, the writer blocks until the reader drains data. This is OS-level backpressure — and the root cause of the classic stdio deadlock.

> [pipe(7) man page](https://man7.org/linux/man-pages/man7/pipe.7.html) — Linux pipe semantics
> [Microsoft Learn: CreatePipe](https://learn.microsoft.com/en-us/windows/win32/api/namedpipeapi/nf-namedpipeapi-createpipe) — Windows
> [IPC Buffer Sizes](https://www.netmeister.org/blog/ipcbufs.html) — cross-platform comparison

### Process.Start and Redirection

`Process.Start` with `RedirectStandardInput/Output/Error` creates anonymous pipes between parent and child. Requires `UseShellExecute = false`.

**The classic deadlock** (three variants, all documented by Microsoft):
1. `WaitForExit()` before `ReadToEnd()` — child's pipe buffer fills, both block.
2. Sequential `ReadToEnd()` on stdout then stderr — stderr fills while reading stdout.
3. Bidirectional stdin/stdout — child blocks on stdout write while parent blocks on stdin write.

> [Microsoft Learn: RedirectStandardOutput](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo.redirectstandardoutput) — deadlock warning
> [Lucian Wischik](https://devblogs.microsoft.com/vbteam/system-diagnostics-process-avoid-deadlocks-in-redirectstandardinputoutput-lucian-wischik/) — detailed analysis

**Handle inheritance pitfall:** Pipe handles are inheritable by default. If the child spawns grandchildren, those inherit the write handle. Stdin EOF detection breaks because the pipe stays open until *all* write handles close.

> [Raymond Chen](https://devblogs.microsoft.com/oldnewthing/20161207-00/?p=94875) — "Why don't I get ERROR_PIPE_BROKEN?"

---

## Framing Protocols

### LSP Base Protocol (`Content-Length` headers)

Each message has HTTP-style headers (ASCII) followed by `\r\n\r\n`, then a UTF-8 body. `Content-Length` is the only required header.

```
Content-Length: 72\r\n
\r\n
{"jsonrpc":"2.0","id":1,"method":"textDocument/completion","params":{}}
```

Binary-safe (body can contain any bytes). Extensible via additional headers. Adopted by LSP, DAP (67+ debug adapters), and BSP.

**Failure mode:** Corrupted `Content-Length` causes permanent desynchronization. No recovery mechanism — no sentinel or sync marker exists.

> [LSP 3.17 Specification](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/) — base protocol
> [DAP Specification](https://microsoft.github.io/debug-adapter-protocol//specification.html) — same framing

### Newline-Delimited JSON (NDJSON)

One JSON object per line, `\n`-delimited. JSON strings must escape literal newlines (`\n`), so `\n` is unambiguous as a message delimiter.

```
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}\n
{"jsonrpc":"2.0","method":"notifications/initialized"}\n
```

Simpler than Content-Length. Debuggable with standard Unix tools. Cannot carry pretty-printed JSON or binary payloads.

**Failure mode:** Truncated line fails JSON parse, but subsequent lines remain parseable — the reader re-synchronizes at the next `\n`. This is a structural advantage over length-prefixed framing.

**Adopted by:** MCP stdio transport, Elasticsearch Bulk API, Docker logs, HL7 FHIR, BigQuery, OpenAI fine-tuning API.

> [NDJSON Specification](https://github.com/ndjson/ndjson-spec) — format spec
> [MCP Transports (2025-06-18)](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports) — MCP's choice of NDJSON

### Length-Prefixed Binary

4-byte big-endian uint32 length, then payload bytes. Two reads per message. Binary-safe. Not human-readable.

gRPC uses a variant: 5-byte header (1 compression flag + 4 length) within HTTP/2 DATA frames. Roslyn's VBCSCompiler uses 4-byte Int32 length prefix with custom binary payload.

**Failure mode:** Corrupted length causes permanent desynchronization, same as Content-Length. A corrupt large value can cause OOM (must enforce max message size).

> [Message Framing (Stephen Cleary)](https://blog.stephencleary.com/2009/04/message-framing.html) — analysis of framing approaches
> [gRPC HTTP/2 Protocol](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md) — gRPC framing

### JSON-RPC 2.0

Application-level protocol, not a framing protocol. Transport-agnostic — defines message structure, not delimitation.

| Message type | Has `id`? | Expects response? |
|-------------|-----------|-------------------|
| Request | Yes | Yes |
| Response | Yes (matches request) | N/A |
| Notification | No | No |

The `id` field is the sole correlation mechanism for multiplexing. Responses may arrive in any order. Batch requests (JSON arrays) are supported.

> [JSON-RPC 2.0 Specification](https://www.jsonrpc.org/specification) — normative

### Framing Comparison

| Property | Content-Length (LSP) | NDJSON | Length-prefixed binary |
|----------|---------------------|--------|----------------------|
| Human-readable on wire | Header yes, body yes (JSON) | Yes | No |
| Binary-safe body | Yes | No | Yes |
| Recovery from corruption | No | Yes (resync at `\n`) | No |
| Stray stdout output | Permanent corruption | Corrupts one line, resync possible | Permanent corruption |
| Pretty-printed JSON | Works (length handles newlines) | Breaks (lines split) | Works |
| Parsing complexity | Medium (scan for `\r\n\r\n`, then exact read) | Low (`readline`) | Low (read 4 bytes, then exact read) |
| Header extensibility | Yes (additional headers) | No | No |

> [Stephen Cleary](https://blog.stephencleary.com/2009/04/message-framing.html) — "If the transmitted length value is corrupted, the remote host will become unsynchronized, and will likely stay that way."

---

## Existing C# Implementations

### OmniSharp (omnisharp-roslyn)

Stdio server for Vim/Neovim. Newline-delimited JSON. Three packet types: Request, Response, Event.

- **Write serialization:** `BlockingCollection<object>` drained by a dedicated background `Thread` via `SharedTextWriter`. Producers enqueue; single consumer writes.
- **Read loop:** `Task.Factory.StartNew` reads lines from stdin. Each request dispatched via fire-and-forget `Task.Factory.StartNew` with exception handling.
- **Error handling:** Exceptions serialized into `ResponsePacket.Message` with `Success = false`.

> [OmniSharp/omnisharp-roslyn](https://github.com/OmniSharp/omnisharp-roslyn) — `src/OmniSharp.Stdio/Host.cs`, `src/OmniSharp.Host/Services/SharedTextWriter.cs`

### OmniSharp.Extensions.LanguageServer (csharp-language-server-protocol)

Reusable C# LSP library. Content-Length framing (LSP standard).

- **Transport:** `PipeReader`/`PipeWriter` from System.IO.Pipelines. Zero-copy parsing.
- **Read loop:** State machine in `InputHandler` scans for `Content-Length:`, `\r\n\r\n`, then reads exact byte count. Multiple messages per buffer read.
- **Built on Rx:** Request tracking via `ConcurrentDictionary`. Response serialization via `Subject<IObservable<Unit>>`.

> [OmniSharp/csharp-language-server-protocol](https://github.com/OmniSharp/csharp-language-server-protocol) — `src/JsonRpc/InputHandler.cs`

### .NET MCP SDK (ModelContextProtocol C#)

Official MCP SDK, co-maintained with Microsoft. NDJSON framing.

- **Serialization:** `JsonSerializer.SerializeToUtf8Bytes()` + `\n` byte + flush.
- **Reading:** `TextReader.ReadLineAsync()`, skip blank lines, `null` = EOF.
- **Stdin workaround:** `CancellableStdinStream` wraps reads with `.WaitAsync(ct)` because native stdin doesn't respect cancellation tokens.
- **Write serialization:** `SemaphoreSlim(1,1)`.
- **Client side:** `Process.Start` with `CreateNoWindow = true`. On Windows, wraps commands with `cmd.exe /c` for shell compatibility. Stderr captured via `ErrorDataReceived` into rolling queue of last 10 lines.
- **Process termination:** `KillTree()` kills entire process tree (necessary because Node.js does not kill children on exit).
- **Lifecycle:** Three-state (`Initial → Connected → Disconnected`) via `Interlocked.CompareExchange`. Cannot reconnect.
- **Encoding:** UTF-8 no-BOM throughout. On .NET Framework, temporarily changes `Console.InputEncoding` under lock.

> [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk) — `StdioClientTransport.cs`, `StdioServerTransport.cs`, `StreamServerTransport.cs`, `TransportBase.cs`
> [dotnet/runtime #100308](https://github.com/dotnet/runtime/issues/100308) — stdin cancellation issue

### Roslyn Compiler Server (VBCSCompiler)

Uses **named pipes**, not stdio. Custom binary protocol with 4-byte Int32 length prefix. 5MB max request size. Pipe name derived from SHA256 hash of (username + admin status + client dir).

- **Startup:** Client acquires named mutex, checks for running server, launches if absent. Creates child suspended, assigns to Job Object, resumes. Timeouts: 5s existing server, 20s new.
- **Resilience:** Silent fallback to in-process compilation if server fails for any reason.
- **Keep-alive:** 600-second (10-minute) idle timeout.

Notable for choosing named pipes over stdio explicitly — avoids stdout contamination entirely.

> [dotnet/roslyn Compiler Server docs](https://github.com/dotnet/roslyn/blob/main/docs/compilers/Compiler%20Server.md) — architecture
> [Roslyn source](https://github.com/dotnet/roslyn) — `src/Compilers/Shared/BuildServerConnection.cs`

### StreamJsonRpc (Microsoft)

Foundational JSON-RPC library used by Visual Studio, ServiceHub, Roslyn LSP, and many Microsoft tools.

- **Transport-agnostic:** Works over `Stream`, `IDuplexPipe`, or `WebSocket`.
- **Pluggable framing:** `HeaderDelimitedMessageHandler` (LSP-style), `LengthHeaderMessageHandler` (4-byte prefix), `WebSocketMessageHandler`.
- **Pluggable serialization:** Newtonsoft.Json, System.Text.Json, MessagePack.
- **For stdio:** `FullDuplexStream.Splice(stdin, stdout)` combines two unidirectional streams. Or `JsonRpc.Attach(stdout, stdin)`.
- **Bidirectional:** Either party can invoke methods on the other.
- **Cancellation:** `$/cancelRequest` notification. `InvokeWithCancellationAsync` aborts transmission if not yet sent, or sends cancel notification if in-flight.

> [microsoft/vs-streamjsonrpc](https://github.com/microsoft/vs-streamjsonrpc) — repository
> [StreamJsonRpc: Connecting](https://microsoft.github.io/vs-streamjsonrpc/docs/connecting.html) — transport setup

### Cross-Cutting Patterns

Every production implementation solves the same problems:

| Concern | OmniSharp | LSP Library | MCP SDK | Roslyn | StreamJsonRpc |
|---------|-----------|-------------|---------|--------|---------------|
| Framing | NDJSON | Content-Length | NDJSON | 4-byte length prefix | Pluggable |
| Write serialization | `BlockingCollection` + thread | Rx `Subject` | `SemaphoreSlim(1,1)` | N/A (named pipe) | Internal `AsyncSemaphore` |
| Read I/O | `TextReader.ReadLine` | `PipeReader` | `TextReader.ReadLineAsync` | `BinaryReader` | `PipeReader` or `StreamReader` |
| Stdout contamination | Routes logs as Event packets | N/A | Logger must not log to Out | Avoids stdio entirely | Explicit warning in docs |
| Encoding | Newtonsoft.Json | System.IO.Pipelines (bytes) | UTF-8 no-BOM | Unicode (`BinaryWriter`) | Pluggable |

---

## Process Lifecycle

### Launch

`Process.Start` with:
- `UseShellExecute = false` (required for redirection)
- `RedirectStandardInput = true`, `RedirectStandardOutput = true`, `RedirectStandardError = true`
- `CreateNoWindow = true` (prevents console window allocation)

The MCP SDK additionally wraps non-cmd.exe commands with `cmd.exe /c {command}` on Windows for shell compatibility.

> [Microsoft Learn: ProcessStartInfo](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.processstartinfo) — configuration

### Monitoring

| Mechanism | Async? | Platform |
|-----------|--------|----------|
| `Process.Exited` event | Yes (thread pool) | Cross-platform |
| `Process.WaitForExitAsync(ct)` | Yes | .NET 5+ |
| `Process.HasExited` polling | No | Cross-platform |

Cancelling `WaitForExitAsync` does **not** terminate the child. Known issue: `WaitForExit` may not return on macOS/Linux when output redirection is used (waits for redirected streams to be fully read).

Exit codes: `0` = success. On Windows, unhandled CLR exceptions produce `0xE0434352`. On Linux, signal kills produce `128 + signal_number` (e.g., SIGKILL = 137).

> [dotnet/runtime #26165](https://github.com/dotnet/corefx/issues/29699) — WaitForExit hang with redirection
> [Microsoft Learn: Process.WaitForExitAsync](https://learn.microsoft.com/en-us/dotnet/api/system.diagnostics.process.waitforexitasync)

### Graceful Shutdown

There is no cross-platform "graceful terminate" in .NET's `Process` class.

| Approach | Platform | Mechanism |
|----------|----------|-----------|
| Shutdown message over stdin | Cross-platform | Application-level. Child must listen for it. Most reliable. |
| `GenerateConsoleCtrlEvent` (CTRL+C) | Windows | P/Invoke. Fragile — targets all processes sharing a console. |
| `kill -SIGTERM` via P/Invoke | Linux/macOS | .NET has no `Process.SendSignal(SIGTERM)` in the BCL. `Process.Kill()` sends SIGKILL. |
| `Process.Kill(entireProcessTree: true)` | Cross-platform (.NET Core 3.0+) | Last resort. Immediate, non-catchable. |

.NET's Generic Host handles SIGTERM automatically. `HostOptions.ShutdownTimeout` defaults to 5 seconds. For parent-to-child, `CancellationToken` does not propagate across process boundaries — an IPC mechanism must trigger a local `CancellationTokenSource.Cancel()`.

> [Nelson Nobre: Graceful Shutdown](https://nelsonbn.com/blog/dotnet-graceful-shutdown/) — patterns
> [Daniel Cazzulino: dotnet-stop](https://www.cazzulino.com/dotnet-stop.html) — cross-platform challenges

### Orphan Prevention (Parent Dies)

| Mechanism | Platform | Triggers on parent crash/kill? | Handles grandchildren? | In .NET BCL? |
|-----------|----------|-------------------------------|----------------------|-------------|
| Job Object + `KILL_ON_JOB_CLOSE` | Windows | Yes | Yes (if in same job) | No (P/Invoke or `Meziantou.Framework.Win32.Jobs` NuGet) |
| `PR_SET_PDEATHSIG` | Linux | Yes | No | No (proposed: dotnet/runtime #101985) |
| Stdin EOF detection | Cross-platform | Yes | No (if handles inherited) | Yes (manual) |

**Job Objects (Windows):** Create with `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`. When the last handle closes (parent death), Windows kills all processes in the job. Put the parent in the job first so children inherit automatically. Since Windows 8, nested job hierarchies are supported.

> [Raymond Chen](https://devblogs.microsoft.com/oldnewthing/20131209-00/?p=2433) — Job Objects for child process cleanup
> [Meziantou](https://www.meziantou.net/killing-all-child-processes-when-the-parent-exits-job-object.htm) — .NET implementation

**`PR_SET_PDEATHSIG` (Linux):** Requests a signal when the parent dies. **Critical caveat:** tracks the parent *thread*, not process. If the ThreadPool retires the thread that called `fork()`, the signal fires while the parent is still healthy. Recall.ai documented this destroying Chromium instances.

> [Recall.ai](https://www.recall.ai/blog/pdeathsig-is-almost-never-what-you-want) — thread tracking pitfall
> [dotnet/runtime #101985](https://github.com/dotnet/runtime/issues/101985) — proposed `ProcessStartInfo.KillOnParentDeath`

**Stdin EOF:** When the parent dies, the OS closes its write handle. The child's next read returns EOF. Reliable cross-platform **if** no other process holds the write handle (handle inheritance to grandchildren breaks it).

> [Raymond Chen](https://devblogs.microsoft.com/oldnewthing/20161207-00/?p=94875) — pipe handle inheritance

### Restart Strategies

Erlang's supervision model provides useful vocabulary:

| Strategy | Behavior |
|----------|----------|
| One-for-one | Only the crashed child restarts |
| One-for-all | All children restart |
| MaxR/MaxT | At most N restarts within T seconds, then escalate |

Practical heuristic: if the child runs longer than a threshold (e.g., 30 seconds), reset backoff. Fast failures increase backoff. This is equivalent to Erlang's intensity model.

Polly's retry patterns (exponential backoff, decorrelated jitter, circuit breaker) are transferable to process supervision even though Polly itself manages HTTP retries, not processes.

> [Erlang Supervisor Behaviour](https://www.erlang.org/doc/system/sup_princ.html) — supervision trees
> [Polly retry strategy](https://www.pollydocs.org/strategies/retry.html) — backoff patterns

---

## Concurrency and Multiplexing

### Request/Response Correlation

JSON-RPC 2.0's `id` field is the standard multiplexing mechanism. Implementation pattern: `ConcurrentDictionary<id, TaskCompletionSource<T>>`. When a response arrives, look up the ID, call `SetResult`. Use `TaskCreationOptions.RunContinuationsAsynchronously` to avoid blocking the reader thread.

> [TaskCompletionSource by Example](https://gigi.nullneuron.net/gigilabs/taskcompletionsource-by-example/) — pattern
> [The Danger of TaskCompletionSource](https://devblogs.microsoft.com/premier-developer/the-danger-of-taskcompletionsourcet-class/) — continuation pitfall

### Write Serialization

Multiple concurrent handlers completing simultaneously must not interleave writes. Three patterns observed in production:

| Pattern | Used by | Mechanism | Batching? |
|---------|---------|-----------|-----------|
| `BlockingCollection` + dedicated thread | OmniSharp | Enqueue/dequeue, single consumer | Possible |
| `SemaphoreSlim(1,1)` | MCP SDK | Async mutex per write | No |
| `Channel<T>` + dedicated task | Modern .NET guidance | Bounded/unbounded async producer-consumer | Yes |

`Channel<T>` is the modern recommended approach. Advantages over `SemaphoreSlim`: natural backpressure with bounded channels, no lock contention, batching opportunity, clean separation of serialization from transport.

> [.NET Blog: Introduction to Channels](https://devblogs.microsoft.com/dotnet/an-introduction-to-system-threading-channels/) — guidance
> [StreamJsonRpc MessageHandlerBase](https://github.com/Microsoft/vs-streamjsonrpc/blob/main/src/StreamJsonRpc/MessageHandlerBase.cs) — internal `AsyncSemaphore`

### System.IO.Pipelines vs Raw Stream

| Property | Raw `Stream` | `PipeReader`/`PipeWriter` |
|----------|-------------|--------------------------|
| Buffer management | Caller allocates, handles partial reads | Automatic pooling, `AdvanceTo` API |
| Backpressure | OS-level only | App-level via `PauseWriterThreshold`/`ResumeWriterThreshold` |
| Allocations | New `byte[]` per read | Pooled from `ArrayPool<byte>` |
| Zero-copy | No | Yes (`ReadOnlySequence<byte>`) |
| Complexity | Low | Medium |

For a single stdio connection, the performance difference is negligible. The advantages are ergonomic (cleaner parsing, no manual buffer management) and architectural (composability with StreamJsonRpc's `PipeMessageHandler`).

> [Microsoft: System.IO.Pipelines](https://devblogs.microsoft.com/dotnet/system-io-pipelines-high-performance-io-in-net/) — design and benchmarks

### Backpressure Chain

When a slow consumer doesn't read: OS pipe buffer fills → host's write blocks → if using Channel, messages queue in memory until bound is hit → producers block on channel write. The entire chain backs up. Unbounded channels cause memory growth. There is no "drop" mechanism unless explicitly configured (`BoundedChannelFullMode.DropOldest`/`DropNewest`).

### Cancellation

StreamJsonRpc and MCP both implement the same pattern: client sends a cancellation notification (`$/cancelRequest` in LSP, `notifications/cancelled` in MCP) referencing the original request `id`. The server may or may not honor it. On the client side, a callback registered on the `CancellationToken` sends the notification and optionally `TrySetCanceled` on the pending `TaskCompletionSource`.

Edge case: if the server doesn't honor cancellation and the connection is lost, the client task may hang without additional timeout logic.

> [StreamJsonRpc: Send Request](https://microsoft.github.io/vs-streamjsonrpc/docs/sendrequest.html) — cancellation protocol
> [StreamJsonRpc #578](https://github.com/microsoft/vs-streamjsonrpc/issues/578) — hung RPC on cancellation + disconnect

---

## Serialization

### Format Comparison

| Property | JSON (System.Text.Json) | MessagePack | Protobuf |
|----------|------------------------|-------------|----------|
| Human-readable | Yes | No | No |
| Schema file required | No | No | Yes (Google.Protobuf) or No (protobuf-net) |
| Payload size (relative) | 1.0× | ~0.6-0.7× | ~0.5-0.65× |
| Serialization speed (relative to STJ) | 1.0× | 2-5× faster | 2-5× faster |
| Self-delimiting | No (needs NDJSON or Content-Length) | Yes (`MessagePackStreamReader`) | No (needs length prefix) |
| AOT/source-gen support | Yes (`JsonSerializerContext`) | Yes (v3 source generator) | Yes (`protoc` codegen) |
| Schema evolution | Informal (type tolerances) | Informal (stable integer keys) | Formal (field numbering, `reserved` keyword) |

> [.NET Serialization Smackdown (Medium)](https://niravinfo.medium.com/net-serialization-smackdown-json-vs-messagepack-vs-protobuf-who-rules-your-bytes-e83027c22cc8) — benchmarks
> [MessagePack-CSharp v3](https://neuecc.medium.com/messagepack-for-c-v3-release-with-source-generator-support-893ed30d0e89) — source generator
> [Protobuf Schema Evolution](https://jsontotable.org/blog/protobuf/protobuf-schema-evolution) — formal guarantees

### Schema Evolution

| Concern | JSON | MessagePack | Protobuf |
|---------|------|-------------|----------|
| Missing field on deserialize | CLR default | CLR default | Default value (0, "", etc.) |
| Unknown field on deserialize | Ignored (default) | Skipped | Preserved (unknown field set) |
| Safe to add fields | Yes | Yes (new integer key) | Yes (new field number) |
| Safe to remove fields | Yes (consumer-dependent) | Yes (don't reuse key) | Yes (reserve number) |
| Risk of silent corruption | Low | Medium (key reuse) | Medium (number reuse) |

### Practical Note

For a parse worker handling files one at a time over a local pipe, serialization time is dominated by the parsing work itself (tree-sitter, Roslyn, etc.). Local pipes deliver GB/s throughput. The performance difference between JSON and binary formats is unlikely to be the bottleneck. JSON's debuggability advantage may outweigh binary formats' size advantage.

---

## Diagnostics

### Logging When Stdout Is the Transport

Any write to stdout that is not a protocol message corrupts the stream. This is the single most common bug in stdio IPC implementations.

**Conventions across tools:**

| Channel | Usage |
|---------|-------|
| stderr | Universal convention for diagnostic output. LSP clients route to output channels. MCP clients capture and optionally log. |
| Protocol-level notifications | LSP: `window/logMessage`. MCP: `notifications/message`. Structured, in-band. |
| Log files | Write to known path (e.g., `.repoql/worker-{pid}.log`). Parent or user tails independently. |

> [MCP Debugging Guide](https://modelcontextprotocol.io/legacy/tools/debugging) — "stdout contains only valid MCP responses, stderr may contain human-readable diagnostics"
> [VS Code LSP Guide](https://code.visualstudio.com/api/language-extensions/language-server-extension-guide) — stderr routing

### Telemetry from Child Processes

**OpenTelemetry OTLP:** Child configures its own `TracerProvider`/`MeterProvider` with an OTLP exporter. The host sets `OTEL_EXPORTER_OTLP_ENDPOINT` in the child's environment, pointing to a local collector (e.g., Aspire Dashboard).

**EventPipe / Diagnostic Ports:** `DOTNET_DiagnosticPorts` configures the child to connect to a named pipe for traces, counters, and GC events. Set by the host before spawning.

> [Microsoft Learn: OTLP example](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/observability-otlp-example) — telemetry setup
> [Microsoft Learn: Diagnostic port](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/diagnostic-port) — EventPipe

### Debugging Child Processes

- `Debugger.Launch()` in child startup code (gate on `--debug` flag). Windows only.
- Attach to Process in Visual Studio (Ctrl+Alt+P) or Rider.
- Child Process Debugging Power Tool (VS 2022 extension) — auto-attaches to child processes.

### Protocol Tracing and Replay

Wrap stdin/stdout with a tee/passthrough that copies all bytes to a log file. Both sides see real traffic; the log is a complete transcript. Replayable: `cat messages.jsonl | worker --stdio > output.jsonl`. This is a natural benefit of stdio — the transport is a file.

> [LSP Inspector](https://github.com/microsoft/language-server-protocol-inspector) — interactive JSON-RPC message visualizer

---

## Memory and GC Pressure

Objects ≥ 85,000 bytes land on the Large Object Heap (not compacted by default). Continuous stdio reading with naive buffer allocation crosses this threshold easily.

**Mitigation patterns:**

| Pattern | Mechanism |
|---------|-----------|
| System.IO.Pipelines | Automatic buffer pooling via `ArrayPool<byte>`. Zero-copy reads via `ReadOnlySequence<T>`. |
| `RecyclableMemoryStream` | Microsoft library. Eliminates LOH allocations via chained small-buffer pool. |
| `ArrayPool<T>.Shared` | Rent/return pattern for temporary buffers. |
| `Span<T>` / `Memory<T>` | Stack-bound (Span, sync) or heap-safe (Memory, async) views without copying. |

**Pipelines gotcha:** `PipeOptions.DefaultMinimumSegmentSize` is only 4,096 bytes. One benchmark showed 2GB copy going from 8607ms to 1771ms by increasing to 65,535 bytes.

> [Microsoft Learn: Large Object Heap](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap) — LOH behavior
> [dotnet/runtime #43480](https://github.com/dotnet/runtime/issues/43480) — MinimumSegmentSize performance

---

## Testing

### In-Process Stream Pairs

`Nerdbank.Streams.FullDuplexStream.CreatePair()` returns two connected `Stream` instances — writes to one appear as reads on the other. Eliminates the need for a real child process in tests.

StreamJsonRpc tests use `FullDuplexStream.Splice(readingStream, writingStream)` to create duplex streams from simplex pairs.

> [Nerdbank.Streams: FullDuplexStream](https://dotnet.github.io/Nerdbank.Streams/docs/FullDuplexStream.html) — API
> [StreamJsonRpc: Connecting](https://microsoft.github.io/vs-streamjsonrpc/docs/connecting.html) — test patterns

### Transport Abstraction Enables Testing

If the worker accepts `Stream`, `IDuplexPipe`, or an `ITransport` interface rather than hardcoding stdin/stdout, tests can inject in-memory transports. The same code runs in-process for tests and out-of-process in production.

### Replay Testing

Save all host-to-worker messages to a `.jsonl` file during normal operation. Replay against a worker process for regression testing. Compare output against expected results.

---

## Transport Alternatives and Abstraction

### When Stdio Is the Wrong Choice

Stdio requires a parent-child relationship. It provides no access control beyond handle inheritance. It cannot support multiple concurrent clients. It conflates transport with process lifecycle.

### Performance Comparison

| Transport | Small messages (~100 bytes) | Large messages (~1MB) |
|-----------|---------------------------|----------------------|
| Named pipes | ~318 Mbits/s | — |
| Unix domain sockets | ~245 Mbits/s | ~41,334 Mbits/s |
| Anonymous pipes (stdio) | — | ~9,039 Mbits/s |

Unix domain sockets show ~350% throughput advantage over anonymous pipes for large messages and work cross-platform (Linux, macOS, Windows 10+).

> [Baeldung: IPC Performance Comparison](https://www.baeldung.com/linux/ipc-performance-comparison) — benchmarks
> [Benchmark: TCP, UDS, Named Pipe](https://www.yanxurui.cc/posts/server/2023-11-28-benchmark-tcp-uds-namedpipe/) — measurements

### Making Transport Swappable

`IDuplexPipe` (`PipeReader Input`, `PipeWriter Output`) is the .NET abstraction for a full-duplex connection. ASP.NET Core's `ConnectionContext` uses it, enabling swappable transports.

StreamJsonRpc accepts `Stream`, `IDuplexPipe`, or `WebSocket`. Switching from stdio to named pipes is a transport-layer change only — no protocol code changes. The `IJsonRpcMessageHandler` interface abstracts framing.

Kestrel natively supports named pipe and Unix domain socket transports since .NET 8. gRPC over named pipes is documented.

> [Marc Gravell: Pipe Dreams Part 2](https://blog.marcgravell.com/2018/07/pipe-dreams-part-2.html) — IDuplexPipe design
> [Microsoft Learn: gRPC IPC with Named Pipes](https://learn.microsoft.com/en-us/aspnet/core/grpc/interprocess-namedpipes) — Kestrel IPC

### Startup Negotiation (Capability Handshake)

Both LSP and MCP use a strict three-step handshake: client sends `initialize` (with protocol version and capabilities) → server responds (with its version and capabilities) → client sends `initialized` notification. No functional requests occur before this completes. The `protocolVersion` field ensures compatibility.

> [LSP Specification 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/) — initialize/initialized
> [MCP Lifecycle](https://modelcontextprotocol.io/specification/2025-03-26/basic/lifecycle) — handshake

---

## Security

### Pipe Visibility

Anonymous pipes (stdio redirection) inherit their ACL from the creator process. Default grants read access to Everyone. Named pipes offer ACL-based access control and endpoint identity verification.

> [Microsoft Learn: Anonymous Pipe Security](https://learn.microsoft.com/en-us/windows/win32/ipc/anonymous-pipe-security-and-access-rights) — ACL defaults

### Secrets in Command Lines

Command-line arguments are visible to any user via `ps -ef` (Linux) or Process Explorer (Windows). Environment variables are not visible in `ps` but are readable via `/proc/<pid>/environ` and inherited by all child processes. Pass secrets via stdin after launch or via files with restrictive permissions.

> [Handling Secrets on the Command Line](https://smallstep.com/blog/command-line-secrets/) — best practices

---

## Gaps

- **`ProcessStartInfo.KillOnParentDeath`** — proposed cross-platform API (dotnet/runtime #101985) has not shipped. Orphan prevention requires platform-specific P/Invoke or stdin EOF detection.
- **`Process.SendSignal(SIGTERM)`** — no BCL API. Graceful cross-platform shutdown requires application-level IPC.
- **Stdin cancellation** — `ReadAsync` on console streams doesn't respect `CancellationToken` (dotnet/runtime #100308). Workarounds required.
- **NDJSON line delimiter** — MCP spec says "newlines" without specifying `\n` vs `\r\n`.
- **NDJSON max message size** — no standard defines limits. Unbounded lines risk OOM.
- **Windows pipe buffer size** — exact default when `nSize=0` is not publicly documented.
- **`PR_SET_PDEATHSIG` thread tracking** — interacts badly with .NET ThreadPool. No .NET-level mitigation documented.
- **Named pipe vs stdio throughput** for typical RepoQL parse payloads (structured JSON, 10KB-1MB range) — not benchmarked.

---

## Summary Tables

### Framing Protocols

| Protocol | Framing | Recovery | Human-readable | Binary-safe | Adopted by |
|----------|---------|----------|----------------|-------------|------------|
| Content-Length (LSP) | Header + exact read | No | Partially | Yes | LSP, DAP, BSP |
| NDJSON | `\n` delimiter | Yes (resync at `\n`) | Yes | No | MCP, Elasticsearch, Docker |
| Length-prefix (4-byte) | Fixed header + exact read | No | No | Yes | gRPC, Roslyn VBCSCompiler |
| MessagePack | Self-delimiting | No | No | Yes | StreamJsonRpc (optional) |

### Existing Implementation Patterns

| Implementation | Framing | Write serialization | Read I/O | Stdout strategy |
|---------------|---------|--------------------|---------|--------------------|
| OmniSharp | NDJSON | `BlockingCollection` + thread | `TextReader.ReadLine` | Logs as Event packets |
| LSP Library | Content-Length | Rx `Subject` | `PipeReader` | N/A |
| MCP SDK | NDJSON | `SemaphoreSlim(1,1)` | `TextReader.ReadLineAsync` | Logger must not log to Out |
| Roslyn VBCSCompiler | Binary length prefix | N/A (named pipe) | `BinaryReader` | Avoids stdio entirely |
| StreamJsonRpc | Pluggable | Internal `AsyncSemaphore` | `PipeReader` or `StreamReader` | Explicit warning in docs |

### Orphan Prevention

| Mechanism | Platform | Reliable? | In .NET BCL? |
|-----------|----------|-----------|-------------|
| Job Object | Windows | Yes | No (NuGet or P/Invoke) |
| `PR_SET_PDEATHSIG` | Linux | Partially (thread issue) | No |
| Stdin EOF | Cross-platform | Yes (if no handle inheritance) | Yes (manual) |
