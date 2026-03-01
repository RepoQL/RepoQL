# Diagnostics: What Great Looks Like

> An agent should be able to see what the system is doing, understand when something is wrong, and fix it.

Claude is exploring a codebase. Every query returns a small footer — trust confirmed, coverage noted, move on. Then a query fails. Claude doesn't see a stack trace. It sees: "Host exited (OOM during embedding). Restart: `::host.restart`. Reduce memory pressure: `::config.set[embed.batch_size, 50]`." Claude restarts the host, waits for the health check to pass, re-runs the query. The user sees a result.

But that's only half the story. Earlier in the session, Claude noticed the footer said `semantic: 72%` and three files were stuck in the processing queue for over a minute. It ran `SELECT * FROM processing_queue() WHERE age_seconds > 60` and found a malformed C++ file causing the parser to hang. It cancelled the stuck item, skipped the file, and continued — before the hang could cascade into a host OOM. The crash never happened.

That's diagnostics. Not a command. A property of the system. Every component is observable, every queue is inspectable, every stuck operation is cancellable, every failure is recoverable. The `::diagnostics` command is one access point. The footer is another. Error messages are another. The SQL surface is another. When any one fails, the others compensate.

---

## Trust

- An agent should be able to confirm result trustworthiness without a separate query
- An agent should be able to see how deeply the system understands each part of the codebase
- An agent should be able to tell whether the index reflects the current state of the working tree
- An agent should be able to see trust status in under 20 tokens, on every response

```
# Healthy — move on:
[ready | semantic: ready | parsed: 94% | 0 pending]

# Trust is qualified — results are partial:
[ready | semantic: 72% | parsed: 87% | 3 pending | 1 failed]

# Trust is broken — results will be incomplete:
[NOT READY — 847 pending, discovery in progress]

# Stale — the index may not reflect reality:
[ready | last scan: 3h ago — working tree may have diverged]
```

The footer is the foundation. Trust has layers: structural readiness, semantic readiness, format coverage (how deeply files are parsed, not just whether they exist), and freshness. A "ready" index built from files that have since changed is not trustworthy, and the footer should say so.

---

## Observability

- An agent should be able to see what the system is doing right now — what's queued, what's in progress, what's blocked
- An agent should be able to identify specific files that are stuck, hanging, or repeatedly failing
- An agent should be able to see how long each in-progress item has been running
- An agent should be able to see resource consumption without leaving the query surface
- An agent should be able to see which other clients are connected and what they're doing

```sql
-- What's happening right now?
SELECT uri, stage, started_at, age_seconds
FROM processing_queue()
ORDER BY age_seconds DESC;
-- file:///src/generated/huge.g.cs | parsing | 14:02:01 | 97s ← stuck

-- What keeps failing?
SELECT uri, attempt_count, last_error
FROM failed_files()
WHERE attempt_count > 1;

-- How are resources looking?
SELECT host_memory_mb, db_size_mb, disk_free_mb
FROM system_resources();
```

Without observability into the queue, the agent's only signal that something is stuck is silence — and silence looks identical to "still working."

---

## Signals

- An agent should be able to understand what went wrong from the error message alone
- An agent should be able to see a recovery action in every infrastructure error
- An agent should be able to distinguish infrastructure failures from user errors
- An agent should be able to recognize version incompatibility between client and host before it causes confusing failures

```
# Great error:
Connection lost: host exited (code 137, OOM).
→ Restart: ::host.restart
→ Reduce memory: ::config.set[embed.batch_size, 50]
→ Full context: ::diagnostics

# Great error:
Query failed: database locked by DBeaver (pid 14209).
→ Close DBeaver or run: ::host.restart

# Great error:
Protocol mismatch: client v1.4.1, host v1.3.2.
→ Redeploy client: deploy.ps1, or rebuild host.

# Bad error:
Grpc.Core.RpcException: Status(StatusCode="Unavailable",
  Detail="Error connecting to subchannel")
```

If an agent needs to run a second command to understand the first error, the first error failed at its job.

---

## Investigation

- An agent should be able to check system health at any depth, paying only for the depth it needs
- An agent should be able to query diagnostic data with SQL — filter it, join it, aggregate it
- An agent should be able to diagnose problems even when the host is unreachable

| Depth | Cost | What you learn | How |
|-------|------|----------------|-----|
| **Glance** | 10 tok | Trust, coverage, freshness | Footer on every response |
| **Check** | 50 tok | Host health, queue depth, resource usage | `SELECT * FROM system_health` |
| **Inspect** | 200 tok | Failed files, stuck items, error details | `SELECT * FROM failed_files()` |
| **Deep dive** | 500+ tok | Environment, connections, logs, config | `::diagnostics` |
| **Offline** | 200 tok | Socket state, PID files, log tail | `::diagnostics` when host is down |

An agent's most common diagnostic need is a boolean. Its second most common is a table it can filter. Full text dumps are the last resort, not the default.

Diagnostic commands may travel through the same infrastructure that is failing. The client needs a local diagnostic path — inspecting sockets, PID files, log tails, and host process state — that never depends on the host being reachable.

---

## Control

- An agent should be able to reliably restart a dead host to a known-good state
- An agent should be able to cancel stuck or hanging operations in the processing queue
- An agent should be able to retry specific failed files without reindexing everything
- An agent should be able to skip known-bad files so they stop poisoning the queue
- An agent should be able to adjust resource configuration at runtime

```
# Reliable restart — the most important recovery primitive:
::host.restart
# Must work when: host crashed, host hanging, socket stale,
# host partially started, previous restart in progress.
# Cannot depend on the host being reachable.

# Queue manipulation:
::queue.cancel[file:///src/generated/huge.g.cs]
::queue.retry[file:///vendor/broken.min.js]
::queue.skip[file:///data/binary.dat]

# Runtime tuning:
::config.set[embed.batch_size, 50]
::config.set[indexing.parallelism, 2]
```

Host restart is the single most critical recovery primitive. If restart is unreliable, all downstream recovery is unreliable. Restart must work from any state without depending on the host being reachable.

Without the ability to cancel a stuck item, the only option is to restart the entire host — killing all in-progress work to unstick one file. Without the ability to skip a known-bad file, it will be retried on every restart, potentially causing the same crash. Surgical control, not just a power switch.

---

## Recovery

- An agent should be able to fix the most common failures without human help
- An agent should be able to verify that a fix worked by re-running the original operation
- An agent should be able to detect when a fix caused a new problem
- An agent should be able to see whether the host is mid-operation before deciding to restart it

```
# The recovery loop:
#   1. Error with embedded diagnosis + suggestion
#   2. Agent checks what the host is doing (mid-embedding? mid-import?)
#   3. Agent runs the suggested recovery
#   4. Agent verifies: ::diagnostics[fast] → "OK" + retry original operation
#   5. Agent continues, mentioning the fix in passing
#
# Verification is the original operation succeeding, not just health returning.
# "I restarted the host" is hope, not evidence.
```

Recovery has a risk spectrum. The agent should know what the host is currently doing before deciding.

| Risk | Agent behavior | Examples |
|------|---------------|----------|
| **None** | Act silently | Read diagnostics, check health, read logs |
| **Low** | Act, then inform | Restart idle host, clean stale socket |
| **Medium** | Inform, then act | Adjust config, reindex failed files, restart busy host |
| **High** | Escalate with evidence | Delete database, kill external processes |

---

## Escalation

- An agent should be able to recognize when a problem exceeds its ability to fix
- An agent should be able to provide what it tried, what it observed, and what it recommends
- An agent should be able to include reproduction steps, environment details, and log locations

```
# Great escalation:
"RepoQL host won't start after two restart attempts.
 Environment: Windows 11, dotnet 9.0.1, RepoQL v1.4.1
 Diagnostics: SocketBindError='permission denied' on /tmp/repoql-abc123
 Tried: ::host.restart (twice), cleaned stale socket, checked port conflicts (none)
 Recommendation: check filesystem permissions on the socket directory.
 Logs: .repoql/host.log
 Note: structural queries are unavailable, but I can still read files directly."

# Bad escalation:
"RepoQL isn't working. Here's the error: [47 lines of stack trace]"
```

---

## Knowledge

- An agent should be able to find troubleshooting guidance through `help://`
- An agent should be able to match symptoms to documented failure modes by searching
- An agent should be able to access core troubleshooting knowledge even when the host is down

```
# Agent encounters unfamiliar error:
explore(keywords="database locked recovery", uriGlob="help://**")
→ help:///troubleshooting/database-lock.md
→ Recovery steps the agent can follow autonomously

# But: help:// is served by the host.
# If the host is down, help:// is unreachable.
# The most critical troubleshooting knowledge — how to recover
# a dead host — must be accessible without a running host.
```

Every failure mode documented in `help://` is a failure mode agents can recover from autonomously going forward. But the most important entries — recovering the host itself — can't live exclusively behind the host.

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| An agent should be able to confirm trust in under 20 tokens, on every response | The most frequent question should be the cheapest |
| An agent should be able to see the processing queue and identify stuck items | Silence is indistinguishable from "still working" without observability |
| An agent should be able to understand an error from the error message alone | A second command to understand the first is a tax on every failure |
| An agent should be able to reliably restart the host from any state | Every recovery path that includes "restart" depends on this |
| An agent should be able to cancel, retry, or skip individual items in the queue | A power switch is not surgical control |
| An agent should be able to diagnose problems even when the host is unreachable | The diagnostic system can't depend on the thing it's diagnosing |
| An agent should be able to verify recovery by re-running the original operation | Hope is not a diagnostic strategy |
| An agent should be able to find troubleshooting knowledge without a running host | Can't look up how to fix the host through the host |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Dump raw diagnostics to the user | An agent should interpret and summarize |
| Require a diagnostic command to understand an error | The error message should be the diagnosis |
| Return 500 tokens to say "everything is fine" | Trust confirmation belongs in the footer |
| Make diagnostic data text-only | An agent should query diagnostics with SQL |
| Treat "ready" as binary | An agent should see coverage, freshness, and format depth |
| Provide only a kill switch for a stuck queue | An agent should have surgical control over individual items |
| Route all diagnostics through the host | An agent should diagnose locally when the host is down |
| Silently fix things repeatedly without surfacing the pattern | Three OOM restarts per session means the human should know |

---

## Relationship to Other North Stars

| North Star | This document's relationship |
|------------|------------------------------|
| **Reliability** | Reliability declares what should never go wrong. This declares what happens when it does. |
| **Commands** | Commands provide the mechanisms for control and recovery. This declares what those mechanisms must be able to do. |
| **Configuration** | Configuration enables runtime adjustment. This declares when and why an agent would adjust it as a recovery strategy. |

---

*The best diagnostic session is the one the user never sees. The second best ends with the agent saying "fixed it, here's what happened." The worst is a stack trace and a shrug.*
