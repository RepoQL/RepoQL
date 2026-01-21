# Diagnostics: What Great Looks Like

> An agent should be able to answer "can I trust this?" and "what's wrong?" without human help.

Diagnostics serve two purposes: **confidence** (before acting) and **debugging** (after failure). Great diagnostics make agents self-sufficient—they diagnose, recover, and proceed without escalation.

---

## The Two Questions

Every diagnostic capability exists to answer one of these:

| Question | When Asked | What "Great" Provides |
|----------|------------|----------------------|
| **"Can I trust this?"** | Before search, before acting on results | Clear yes/no with reason if no |
| **"What's wrong?"** | After error, unexpected results | Actionable diagnosis with recovery path |

If the agent can't answer these questions autonomously, diagnostics have failed.

---

## Capsule: ConfidenceCheck

**Invariant**
Before acting on query results, the agent must be able to confirm: data is complete, nothing failed silently, the answer reflects reality.

**Example**
```sql
-- Great: Single query, clear answer
SELECT ready, semantic_ready, pending, failed
FROM indexing_confidence();
-- ready: true, semantic_ready: true, pending: 0, failed: 0

-- Current: Requires interpretation
SELECT indexing_diagnostics();
-- status: idle, epoch: 4082, embed_last_epoch: 3407...
-- Agent must infer: "is 3407 vs 4082 a problem?"
```
//BOUNDARY: If the agent must interpret or calculate, confidence is uncertain.

**Depth**
- `ready` = structural queries are trustworthy (discovery complete, no pending hot-path)
- `semantic_ready` = search() will work correctly (embeddings caught up)
- `pending` = files still being processed (non-zero means results may be incomplete)
- `failed` = files that couldn't be indexed (non-zero means gaps exist)
- One query, boolean answers, no interpretation required

---

## Capsule: FailureDiagnosis

**Invariant**
After any failure, the agent can determine: what failed, why, and what to do about it.

**Example**
```
Great:
  Error: Connection lost to host
  Diagnosis: Host process exited (code 1) - out of memory during embedding
  Recovery: Restart with REPOQL_EMBED_BATCH_SIZE=100, or disable embeddings

Current:
  Error: Connection lost to host
  [50 lines of stack trace and stderr]
  Agent must: parse output, identify cause, guess recovery
```
//BOUNDARY: Diagnosis without recovery suggestion is incomplete.

**Depth**
- Error classification: infrastructure vs user-input (already exists)
- Root cause extraction: parse stderr/logs to identify actual cause
- Recovery mapping: known causes → specific recovery actions
- Escalation threshold: "if cause unknown, suggest human review"

---

## Capsule: ProgressiveDepth

**Invariant**
Quick check for confidence; deep dive only when needed.

**Example**
```
Level 1 - Status (10 tokens):
  ready: true, semantic: true, pending: 0

Level 2 - Summary (100 tokens):
  status: idle, epoch: 4082, embed_epoch: 4082
  queues: hot=0, idle=0, analysis=0
  health: connected, serving

Level 3 - Full diagnostics (500+ tokens):
  [Everything: env, paths, host output, connection details...]
```
//BOUNDARY: Don't pay 500 tokens to learn "everything is fine."

**Depth**
- Level 1: Boolean confidence check — most common need
- Level 2: Numeric status — "how far behind?" "how much pending?"
- Level 3: Full diagnostic dump — debugging connection issues, crashes
- Current `:diagnostics:` is Level 3 only; missing quick checks

---

## Capsule: StructuredOverText

**Invariant**
Diagnostic data should be queryable, not just printable.

**Example**
```sql
-- Great: Join diagnostics with other data
SELECT f.uri, f.error_count, d.parse_error
FROM Files f
LEFT JOIN failed_files() d ON f.uri = d.uri
WHERE d.uri IS NOT NULL;

-- Current: Text blob, can't join or filter
SELECT indexing_diagnostics();
-- "status: idle\nepoch: 4082\n..."
```
//BOUNDARY: If you can't WHERE/JOIN/GROUP on it, it's not truly queryable.

**Depth**
- `indexing_status` view: columns not key-value text
- `failed_files()` table function: what failed and why
- `pending_files()` table function: what's still in queue
- Text format for human debugging; structured for agent automation

---

## Capsule: ProactiveSurfacing

**Invariant**
Problems should surface before they cause harm, not after.

**Example**
```
Great:
  Query result footer: "⚠ 3 files failed to index (run failed_files() to see)"
  Search result: "Note: embeddings 12% behind current epoch"

Current:
  Results return normally
  Agent discovers problem later when results seem wrong
```
//BOUNDARY: Silent incomplete results violate reliability principles.

**Depth**
- Query footer already shows `index: N pending` — good start
- Missing: failed file count, embedding lag warning
- Threshold-based: warn when pending > 0, failed > 0, embed lag > 10%
- Don't spam: only warn when it affects result trustworthiness

---

## The Diagnostic Hierarchy

| Level | Purpose | Trigger | Output |
|-------|---------|---------|--------|
| **Passive** | Confidence footer on every query | Automatic | `[ready: ✓ | semantic: ✓ | 0 pending]` |
| **Quick check** | "Am I good to proceed?" | `SELECT * FROM indexing_status` | Single row, boolean columns |
| **Status query** | "What's the current state?" | `indexing_diagnostics()` | Key-value summary |
| **Deep dive** | "What's wrong and why?" | `:diagnostics:` | Full environment, connection, host output |
| **Failure analysis** | "What files have problems?" | `SELECT * FROM failed_files()` | Table of URIs with error messages |

---

## What "Great" Looks Like

| Capability | Great | Current | Gap |
|------------|-------|---------|-----|
| **Confidence check** | `SELECT ready FROM indexing_status` → `true` | Parse `indexing_diagnostics()` text | Need structured view |
| **Semantic readiness** | `semantic_ready` boolean | Compare `embed_last_epoch` vs `epoch` | Need derived boolean |
| **Failed files** | `SELECT * FROM failed_files()` | `last_error` shows most recent only | Need table function |
| **Pending files** | `SELECT * FROM pending_files()` | `indexing_queue()` JSON array | Exists, could be cleaner |
| **Auto-diagnostics** | Appended on infrastructure error | ✓ Already works | — |
| **Recovery suggestions** | "Try: restart host with X flag" | Stack traces only | Need cause→recovery mapping |

---

## Anti-Patterns

| Anti-Pattern | Why It's Bad | What To Do Instead |
|--------------|--------------|-------------------|
| Text-only diagnostics | Can't query, filter, join | Provide structured views |
| All-or-nothing depth | 500 tokens for "yes it's fine" | Progressive disclosure levels |
| Raw stderr dumps | Agent must parse stack traces | Extract cause, suggest recovery |
| Implicit confidence | Agent assumes results are complete | Explicit ready/not-ready signal |
| Reactive only | Problems found after bad decisions | Proactive warnings in footers |

---

## Key Additions Needed

| Addition | Purpose | Effort |
|----------|---------|--------|
| `indexing_status` view | Structured confidence check | Low |
| `semantic_ready` boolean | Clear search trustworthiness | Low |
| `failed_files()` function | What couldn't be indexed | Medium |
| Recovery suggestions | Map known errors to fixes | Medium |
| Footer warnings | Proactive problem surfacing | Low |

---

## Measurement

| Metric | Target |
|--------|--------|
| Confidence check cost | <20 tokens |
| Time to "can I trust this?" | One query, instant |
| Recovery suggestion rate | >80% of known failure modes |
| Human escalation rate | <5% of diagnostic sessions |

---

*Great diagnostics answer "can I trust this?" in 10 tokens and "what's wrong?" with a fix.*
