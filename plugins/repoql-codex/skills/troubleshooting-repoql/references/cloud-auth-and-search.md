# Cloud auth, embeddings & search

The Services layer that needs the cloud: **`explain`** and the `ask()` synthesis use cloud inference; **semantic search** quality leans on embeddings. When these "silently do nothing" or come back shallow, check auth and embedding state before blaming the feature.

## Auth first

```
command("account whoami")
```

No session or an expired one ⇒ log in:

```
command("account login")                    # browser loopback flow (default)
command("account login --mode device-code") # SSH / containers / no browser
command("account login --mode browser")     # force the browser flow explicitly
```

Note the flag is `--mode device-code`, **not** `--device-code`. Auth state is read from the local session store, so `account whoami` works even when the host is misbehaving. Deeper: `help:///operations/cloud-auth.md`, `help:///commands/auth.md`.

## What needs the cloud, and what degrades gracefully

| Feature | Needs cloud? | When unavailable |
|---|---|---|
| `explain`, `read … => question`, `ask()` | Yes (inference) | Fail / shallow — there is no local fallback |
| Semantic search ranking | Cloud embeddings preferred | Falls back to local ONNX embeddings + BM25/fuzzy — degraded, not broken |
| Lexical search, structure, `query`, `read` | No | Unaffected |

So: `explain` returning shallow or erroring → check `account whoami`. Semantic search returning *nothing* is more often an **embedding-progress** problem than an auth one.

## Is embedding actually done?

```sql
SELECT state, semantic_percent, semantic_ready, structure_embedded, complete, total_files
FROM engine_status;
```

`semantic_ready` is true only when **every** file is at least structure-embedded; until then semantic ranking is partial and search may lean on lexical matching. `semantic_percent` is your progress bar. Per-file embedding flags live in `indexing_registry` (`structure_embedded`, `full_text_embedded`) if you need to find the stragglers.

If embeddings are stuck at `0%` long after indexing finished, the embedding service may be unreachable or unconfigured — local ONNX still produces vectors (lower quality), so search works but ranks worse. Re-check auth and network; `command("diagnostics memory")` shows the embedding footprint.

## Boundary

If search returns nothing because the **content isn't indexed yet**, that's the indexing layer (`indexing-and-coverage.md`), not the cloud. Read the trust footer: `semantic: N%` low ⇒ embeddings; `index: N%` low ⇒ still indexing.
