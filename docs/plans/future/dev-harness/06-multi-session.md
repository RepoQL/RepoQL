---
description: Sixth increment - coordination between multiple Claude sessions
tags: [dev-harness, plan, multi-session, coordination]
audience: { human: 30, agent: 70 }
purpose: { plan: 95, design: 5 }
---

# Plan: Multi-Session Coordination

Implements: [Dev Harness Design](../../designs/future/dev-harness.md) — Session coordination, conflict detection

## Scope

**Covers:**
- Conflict detection for concurrent build/deploy operations
- `harness.wait_for_operation` tool
- Session information in status
- Operation attribution

**Does not cover:**
- Direct harness-to-harness communication (not needed)

## Architecture

Multiple harness instances coordinate via Aspire's shared visibility:
- Each harness can see host state via Aspire
- Operation locks stored in shared location (file or Aspire resource)
- Conflicts detected before operation starts

## Enables

Once multi-session coordination exists:
- **Parallel work** — Multiple Claude sessions without conflicts
- **Visibility** — Each session knows what others are doing
- **Explicit coordination** — Wait or proceed with awareness

## Prerequisites

- Plan 04 complete (lifecycle operations working)

## Done Criteria

### Conflict Detection

- Before starting build/deploy, the system shall check for existing operation
- Operation lock stored at: `.repoql/harness-operation.lock`
- Lock file contains: `{ "session_id": "...", "operation": "building", "started_at": "..." }`
- When lock exists and is recent (< 5 minutes), return conflict error:
  ```json
  {
    "error": "operation_in_progress",
    "message": "Another session is building",
    "conflict": {
      "session_id": "sess_xyz789",
      "operation": "building",
      "started_at": "2026-02-05T14:30:00Z"
    },
    "options": ["harness.wait_for_operation()", "wait and retry"]
  }
  ```

### harness.wait_for_operation Tool

- When called, poll for operation completion every 2 seconds
- When operation completes (lock released), return success:
  ```json
  {
    "waited_for": "building",
    "session_id": "sess_xyz789",
    "waited_ms": 15000
  }
  ```
- Timeout after 5 minutes with error

### Session Info in Status

- The `harness.status` shall include session coordination info:
  ```json
  {
    "harness": { "session_id": "sess_abc123", ... },
    "coordination": {
      "operation_in_progress": true,
      "operating_session": "sess_xyz789",
      "operation": "building"
    }
  }
  ```

### Operation Attribution

- When operation completes or fails, log which session triggered it
- Useful for debugging "who broke the build"

## Constraints

- **File-based locking** — Simple, works without additional infrastructure
- **Best-effort coordination** — If lock file inaccessible, warn but proceed
- **No queue** — Agent decides whether to wait or do something else

## Implementation Notes

- Use file locking with atomic create (fail if exists)
- Clean up stale locks (> 5 minutes old)
- Lock file in `.repoql/` directory (per-repository)

## Verification

1. Start two harness instances
2. Trigger build in first, immediately try build in second
3. Verify second gets conflict error
4. Use `harness.wait_for_operation()` in second, verify it waits
5. Verify both complete successfully
