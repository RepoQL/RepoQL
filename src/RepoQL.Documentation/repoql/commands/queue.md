---
description: "Control one queued/failed URI with cancel, skip, and retry commands."
tags: ["queue", "cancel", "skip", "retry", "commands", "diagnostics"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::queue

Queue commands provide surgical control over one file URI:

- `::queue.cancel[uri]` marks a queued/in-flight URI as failed (`Cancelled by user`).
- `::queue.skip[uri]` marks a URI as skipped and persists it to `.repoql/skip-list.txt`.
- `::queue.retry[uri]` resets failed/skipped URI back to discovered.

---

## Capsule: `::queue.cancel[uri]`

**Invariant**
Cancel is stage-boundary based. If a stage is already running, it completes first, then remaining stages are skipped.

**Example**
```text
::queue.cancel[file:///src/generated/Huge.g.cs]
→ Cancelled: file:///src/generated/Huge.g.cs (was Indexing in HotPath)
```

---

## Capsule: `::queue.skip[uri]`

**Invariant**
Skip persists across restarts via `.repoql/skip-list.txt`. Skipped files are intentionally excluded from processing.

**Example**
```text
::queue.skip[file:///vendor/broken.min.js]
→ Skipped: file:///vendor/broken.min.js (will not be processed)
```

---

## Capsule: `::queue.retry[uri]`

**Invariant**
Retry applies to `Failed` or `Skipped` URIs. Retrying a skipped URI also removes it from `.repoql/skip-list.txt`.

**Example**
```text
::queue.retry[file:///vendor/broken.min.js]
→ Re-enqueued: file:///vendor/broken.min.js (previous: Skipped, error: Skipped by user)
```

---

## Verification Queries

Use these after queue actions:

```sql
SELECT * FROM processing_queue();
SELECT * FROM failed_files();
```
