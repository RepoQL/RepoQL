# Reliability: What Great Looks Like

> An agent using RepoQL should never be surprised by missing data, silent failures, or stale results.

Reliability in RepoQL means the agent can trust what they see. When a search returns nothing, it means nothing exists—not that something failed silently. When data is present, it reflects reality—not a partial index from a crashed run. When a query completes, the agent can act on the result with confidence.

---

## The Cardinal Rule

**Never silently return incomplete results.**

```
Loud failure     → Agent retries or escalates → Problem visible
Silent partial   → Agent acts on bad data    → Cascading harm
```

A thrown exception is better than a result that looks correct but isn't. Every reliability mechanism exists to either prevent bad data or make problems visible.

---

## Capsule: DataIntegrity

**Invariant**
Single-writer architecture: all writes go through one process. Parallel writes corrupt the database.

**Example**
```
DuckDB file + ReaderWriterLockSlim + gRPC host
   └── Any client can query
   └── Only host can write
   └── Coordination is automatic
```
//BOUNDARY: If two processes touch the DuckDB file directly, corruption is inevitable.

**Depth**
- DuckDB enforces single-writer at the file level—no concurrent transactions from multiple processes
- `DuckDbDataStore` wraps all access with `ReaderWriterLockSlim`
- Host process owns the database; clients send requests via gRPC
- Corruption recovery is expensive: full reindex from scratch
- NotThis: "just be careful"—architecture must make wrong usage impossible

---

## Capsule: QueryConfidence

**Invariant**
A query result is trustworthy only when the agent knows: (a) indexing is complete, (b) the query executed correctly, (c) no failures were swallowed.

**Example**
```
Agent: "Find all usages of AuthService"
Great: Returns 47 usages from 23 files. Agent knows index is complete.
Bad:   Returns 12 usages. Index was 25% done. Agent doesn't know.
Worst: Returns 12 usages with no indication. Agent refactors, breaks 35 call sites.
```
//BOUNDARY: Incomplete results presented as complete cause more damage than errors.

**Depth**
- `QueryBarrier` blocks queries until minimum indexing threshold is met
- Semantic search waits for embeddings; structural queries wait for file scan
- Queries against stale/partial indexes should either block or clearly indicate incompleteness
- "I found nothing" must mean "nothing exists" not "I haven't looked yet"

---

## Capsule: FailureTransparency

**Invariant**
Every failure path must either recover automatically or surface visibly to the caller.

**Example**
| Failure Mode | Great | Bad |
|--------------|-------|-----|
| Host unreachable | Reconnect automatically, retry once | Silent timeout, partial result |
| Query syntax error | Clear error with position | Generic "query failed" |
| File parsing error | Annotation on file, other files indexed | Whole batch fails silently |
| Embedding service down | Queries block or clearly warn | Semantic search returns nothing |

//BOUNDARY: "Fire and forget" is never acceptable for operations that affect query results.

**Depth**
- Heartbeat failures that go unnoticed → lease expires → client thinks it's connected → queries fail unexpectedly
- Index errors on one file shouldn't block others—emit annotation, continue
- Network failures should reconnect transparently up to a point, then fail loudly
- The agent should never wonder "did that work?"

---

## Capsule: ProgressVisibility

**Invariant**
The agent can always determine: what's indexed, what's in progress, what's blocked.

**Example**
```sql
-- "Can I trust the search results?"
SELECT stage, progress, blocked_reason
FROM indexing_status;

-- "What files aren't indexed yet?"
SELECT uri FROM pending_files;

-- "Why is this query slow?"
SELECT barrier_waiting_for FROM query_status;
```
//BOUNDARY: If the agent can't answer "is this data complete?" the answer is effectively "no."

**Depth**
- Indexing stages: Discovery → Parsing → Semantic → Analysis
- Each stage has observable progress (file counts, percentages, ETAs are lies)
- Blocked queries should report why they're blocked
- Stale data should be visibly stale (timestamps, version indicators)

---

## Capsule: GracefulDegradation

**Invariant**
Partial capability is better than no capability, but only when the limitation is visible.

**Example**
| Situation | Graceful | Not Graceful |
|-----------|----------|--------------|
| Embeddings not ready | Structural queries work; semantic search blocks or warns | Everything waits |
| One parser crashes | Other formats indexed; broken files get annotations | Whole batch fails |
| External MCP down | Local features work; external queries fail clearly | Silent empty results |
| Low memory | Reduced parallelism, slower; still correct | OOM crash mid-index |

//BOUNDARY: Degraded mode must be observable—agents adjust their strategy when they know limitations.

**Depth**
- Core structural queries should work before semantic features
- Parse errors on individual files shouldn't cascade
- Resource constraints should slow things down, not corrupt them
- The agent should be able to ask "what's not working right now?"

---

## Capsule: ConnectionReliability

**Invariant**
Client-host communication survives transient failures; permanent failures surface quickly.

**Example**
```
Transient (auto-recover):
  - Host restart during idle time
  - Network blip
  - Brief resource exhaustion

Permanent (fail fast):
  - Host can't start (port in use, corrupt database)
  - Incompatible versions
  - Configuration error
```
//BOUNDARY: Retry budget must be finite. Infinite retry = infinite hang.

**Depth**
- Reconnection should be automatic and transparent up to a threshold
- Beyond threshold, fail loudly with diagnostics
- 120-second startup timeout is too long for interactive use; consider circuit breakers
- The lease stream is the heartbeat—application-level heartbeats add complexity without reliability
- Race conditions in launch (two clients, both start host) should be handled, not ignored
- SeeAlso: `docs/flows/host-client-architecture.md`

---

## Capsule: RecoveryPaths

**Invariant**
Every failure state has a documented recovery path; catastrophic failures have automated recovery.

**Example**
| State | Recovery |
|-------|----------|
| Stale socket file | Auto-detect, rename, clean up |
| Orphaned host | New `serve` shuts down old host |
| Corrupt database | Delete `.repoql/`, reindex (automated detection: future) |
| Failed lease | Reconnect transparently |
| Partial index | Resume from checkpoint, not restart |

//BOUNDARY: "Delete everything and start over" is not a recovery path—it's an admission of failure.

**Depth**
- Recovery should be automatic where possible
- Recovery should be documented where automation isn't feasible
- Recovery should preserve as much state as possible
- Time to recovery matters—minutes not hours
- Corruption detection should be proactive, not discovered by symptoms

---

## The Reliability Hierarchy

When reliability requirements conflict, prioritize in this order:

1. **Data integrity** — Never corrupt the database
2. **Result correctness** — Never return wrong data as if it were right
3. **Failure visibility** — Never hide problems from the agent
4. **Availability** — Keep working when possible
5. **Performance** — Fast is good, correct is mandatory

---

## What "Great" Looks Like

| Dimension | Great | Acceptable | Unacceptable |
|-----------|-------|------------|--------------|
| **Integrity** | Corruption impossible by design | Corruption detected and recovered | Silent corruption |
| **Confidence** | Agent always knows completeness status | Agent can query completeness | Agent guesses |
| **Transparency** | All failures surface with context | Failures surface eventually | Silent failures |
| **Progress** | Observable stages with metrics | "Working..." indicator | No feedback |
| **Degradation** | Partial features with clear limits | Full features or nothing | Partial results as complete |
| **Connection** | Transparent recovery from transient | Manual reconnect works | Hung connections |
| **Recovery** | Automated from all known failures | Documented manual steps | "Delete and restart" |

---

## Anti-Patterns

| Anti-Pattern | Why It's Bad | What To Do Instead |
|--------------|--------------|-------------------|
| Fire-and-forget heartbeats | Client doesn't know lease is dead | Stream closure = lease end |
| Swallowed exceptions | Failures invisible | Log, annotate, or propagate |
| "Timeout after 2 minutes" | Too long for interactive, too short for big repos | Adaptive with progress feedback |
| Infinite retry | Hangs forever on permanent failure | Finite budget with diagnostics |
| "Works on my machine" | Fails on WSL, Windows, different configs | Test matrix for all platforms |
| Best-effort indexing | Agent can't trust results | Complete or clearly incomplete |

---

## Measurement

How to know if reliability is great:

| Metric | Target |
|--------|--------|
| Silent failure rate | 0% — every failure visible |
| Recovery automation | >90% — manual intervention rare |
| Time to detect corruption | <1 query — caught before damage |
| Reconnection success rate | >99% for transient failures |
| Agent confidence questions | Answerable via query, not docs |

---

## Key Files

| File | Reliability Role |
|------|-----------------|
| `DuckDbDataStore.cs` | Single-writer enforcement |
| `RepoQlClient.cs` | Connection, lease, reconnection |
| `QueryBarrier.cs` | Blocks queries until index ready |
| `LeaseRegistry.cs` | Tracks active clients |
| `IdleShutdownHostedService.cs` | Clean shutdown |

---

## What This Document Doesn't Cover

- **Performance tuning** — Speed vs correctness tradeoffs
- **Scaling limits** — Repository size boundaries
- **Specific error codes** — See reference documentation
- **Implementation details** — See flow and design docs

---

*An agent should never have to ask "can I trust this result?" The answer should always be yes—or the query should have failed.*
