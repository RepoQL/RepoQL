---
name: troubleshooting-repoql
description: "Diagnose RepoQL problems on the remote uplink connection. Use when a RepoQL tool call fails or errors, the RepoQL tools are missing or need sign-in, the server will not connect, an uplink is unknown, offline, or missing from the list, access is denied or revoked, a repository or recent change is not found, results seem wrong or incomplete, or semantic search or explain returns nothing."
tags: ["skill", "diagnostics", "troubleshooting", "uplink", "remote", "sign-in", "access", "errors", "indexing", "mcp", "repoql"]
---

# Diagnose RepoQL over an uplink

Every call crosses five layers. A fault in an early layer shows up as a symptom in a later one: an expired sign-in looks like a missing tool, an offline uplink looks like a failed search. **Confirm the layer before the one you suspect.**

```
Connection  →  Access  →  Destination  →  Host  →  Index
(endpoint,     (account,   (which uplink,  (its RepoQL  (what it holds,
 sign-in)       organization) online?)      version)     how complete)
```

This file is complete on purpose. Nothing here loads through the connection it diagnoses.

---

## Know these first

### 1. You can see, but you cannot fix

This connection is read-only, and nothing runs on this machine. There is no host to restart, no log to read, no setting to change. Each layer has an owner, and the fix belongs to them:

| Layer | Owner | Their fix |
|---|---|---|
| Connection | The user | Sign in through `/mcp`; correct the endpoint |
| Access | The organization's RepoQL admin | Grant or restore uplink access |
| Destination, Host | The uplink's operator | Bring the host online; update its RepoQL |
| Index | The uplink's operator, or time | Import the repository; let indexing finish |

Your job is to find the layer, say what you observed, and name the owner. Do not retry in a loop, and do not send the call to a different uplink as a substitute — a different index gives a confident answer about different code.

### 2. `uplinks()` is the first diagnostic

```
uplinks()
```

One call separates three layers. If it fails, the fault is Connection or Access. If it succeeds, it names the organization, the sign-in default, and each accessible uplink as `connected` or `offline` — which settles Destination. Use the names exactly as returned.

### 3. Split your input from the infrastructure

A malformed query is the most common failure, and the host explains it: a bad column lists `Candidate bindings:`, a bad view says `Did you mean …?`, a read that matched nothing says what did not match. `Parser`, `Binder`, `Catalog`, and `Conversion Error` are your input. Read the message, run `DESCRIBE SELECT * FROM <view>`, or read `help:///schema/**`, and fix the call. The SQL surface also refuses `SET`, `PRAGMA`, writes, and multiple statements by design.

### 4. The trust footer is a free signal

Every `query`, `read`, and `explore` response ends with a footer from the uplink's host:

```
[2.9k tok | 239 ms | ready]                                               ← settled
[108 tok | 8 ms | index: 57% (2823 pending) | semantic: 79% | stale: 1376]  ← still filling
```

Incomplete results under `index: 57%` are the index catching up, not a fault. The same state is one row: `SELECT * FROM engine_status`.

### 5. The index holds pushed code from the repositories it was given

`file:///` is the uplink host's workspace, never this machine. The user's uncommitted edits are not in any uplink. A recent push may still be indexing. A repository nobody imported is not there. Check which of these applies before concluding code does not exist.

---

## Diagnose by symptom

Start at the first move. Escalate only if it does not explain the symptom.

| Symptom | Layer | First move | Then |
|---|---|---|---|
| No RepoQL tools, or the server needs authentication | Connection | Tell the user to run `/mcp`, select the RepoQL server, and sign in | You cannot start the sign-in flow |
| The server fails to connect, or the endpoint is not found | Connection | Confirm the endpoint with the user | `REPOQL_UPLINK_URL` sets another shared URL ending in `/uplink`, before Claude Code starts. A URL for one specific uplink is the wrong kind |
| "The shared uplink connector is not configured" | Connection | Report it; the service operator must enable the shared connector | Do not retry |
| A refusal that names the organization or role, with no sign-in prompt | Access | The account is signed in but has no active role here | The organization admin grants it; signing in again does not |
| "This connection is no longer authorized. Sign in again." | Access | The user signs in again through `/mcp` | If it repeats, access was revoked — the admin restores it |
| "Uplink access could not be verified. Retry shortly." or a 503 | Access | Retry once after a short wait | The membership check is temporarily unavailable; the sign-in is still good |
| `uplinks()` lists nothing, or an expected uplink is missing | Access | Check the organization in the result | Wrong organization → sign in again and choose it. Right organization → the admin grants access |
| "No default uplink was selected at sign-in" | Destination | `uplinks()`, then pass `uplink="<name>"` | To set a default, the user signs in again |
| "Unknown uplink" | Destination | `uplinks()`; use a returned name | A name from another organization needs a new sign-in |
| An uplink is `offline`, or a call says it is unreachable | Destination | Report the uplink by name | Its operator restores it; signing in again does not |
| A tool is unsupported, or a parameter is unrecognized, on one uplink | Host | That uplink's host runs an older RepoQL than this connection expects | Its operator updates it. `query` often still works there |
| A `query` or `read` errors with `Binder`, `Catalog`, `Parser` | Your input | Read the enriched error; `DESCRIBE` the view | Not an infrastructure fault |
| A repository is not found | Index | `SELECT source_uri, file_count FROM Filesystems ORDER BY source_uri` on that uplink | Absent → it was never imported there; try the uplink that holds it, or ask the operator |
| Results look incomplete | Index | Read the footer; `SELECT * FROM engine_status` | Still filling → say so and scope the answer |
| A recent change is not reflected | Index | Confirm it was pushed; read the footer `stale:` count | Unpushed work is in no uplink |
| A specific file is missing or wrong | Index | `SELECT uri, reason, error FROM indexing_registry WHERE failures > 0` | Report the file and error to the operator |
| Semantic search is empty, or `explain` is shallow | Index | Check `semantic_percent` in `engine_status` | Low → embeddings are still filling; structural queries already work |

A `read` or `explore` that returns no match is usually not a failure. The response says why and what to try. Read it again before escalating.

The host describes its own diagnostic views at `help:///schema/views/**`. Prefer that, and `DESCRIBE`, over any column name memorized here.

---

## Recovery discipline

- **Cheapest first.** The error text → `uplinks()` → the footer → `engine_status` → the scoped view.
- **Name the owner.** A report that says "the `platform` uplink is offline; its operator needs to restore it" is finished. A report that says "RepoQL is not working" is not.
- **Never substitute.** An answer from the wrong uplink is worse than no answer. Say what could not be reached.
- **Verify by re-running the call that failed**, not by seeing `uplinks()` succeed.
- **Say what the answer rests on.** When the index is partial or an uplink was unreachable, carry that into the answer.

---

*Find the layer. Name the owner. Never answer from a different index.*
