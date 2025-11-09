# Hybrid Semantic Search – AST Channel (Follow-on Design)

> This document extends `hybrid-semantic-search.md` with the optional AST pattern channel. It does **not** block the base hybrid search rollout.

---

## Motivation

Some queries (stack traces, `sg:` patterns, `$CAPTURE` macros) require syntactic matches across languages. We want to:
- Let users/agents submit structural patterns and get matching objects.
- Fuse AST matches with lexical/dense results when available.
- Keep storage and query costs bounded so repos without AST data still work.

---

## Proposed Storage Extensions

1. **AST snapshots per document**
   - Persist a compressed representation (e.g., tree-sitter S-expressions or AST-grep serialized format) keyed by `doc_id`.
   - Optionally store only the subset needed for pattern-matching (node kinds + span ranges) to reduce size.

2. **Match index**
   - Maintain a table `ast_match(doc_id UUID, pattern TEXT, uri TEXT, kind TEXT, line_start INT, line_end INT, captures JSON, updated_at TIMESTAMP)` populated lazily:
     - On pattern execution, cache the results keyed by `{doc_id, pattern}`.
     - Evict cache entries when the document digest changes.

3. **Metadata hooks**
   - Extend `repo_index` to flag rows that have AST coverage so the router can short-circuit when AST isn’t available.

---

## Query Surface

- Extend the `search` macro with `mode='ast'` (explicit) and router heuristics (`sg:` prefix, `$CAPTURE`).
- Add a helper macro `astgrep(pattern TEXT, uri_glob TEXT NULL, mime_glob TEXT NULL, limit INT DEFAULT 500)` that returns raw matches for tooling/testing.

### Macro behavior
1. Resolve eligible documents via glob filters and language hints.
2. Fetch AST snapshots for those documents (paged/batched to cap memory).
3. Run the AST pattern engine (tree-sitter query, AST-grep, or our own matcher).
4. Convert matches into object URIs + spans using the existing node table.
5. Feed matches into the RRF pipeline as an additional channel with score `ast_score = 1 / (rank + k0)`.
6. Record diagnostics in `explain_json.ast = {status: 'ok'|'missing_ast'|'invalid_pattern', docs_scanned, cache_hit}`.

---

## Implementation Options

### Option A: Tree-sitter queries
- Store tree-sitter bytecode per language.
- Pros: battle-tested, good performance.
- Cons: more work to manage per-language grammars and query compilation.

### Option B: AST-grep snapshots
- Serialize AST-grep’s intermediate representation during indexing so queries just run in-process.
- Pros: aligns with `sg:` syntax many users already know.
- Cons: larger on-disk footprint; need to embed AST-grep runtime.

### Option C: Hybrid
- Use tree-sitter for languages we already parse, fall back to AST-grep for text-like formats.

---

## Sequencing
1. Land the base hybrid search (lexical + dense) per the main proposal.
2. Add AST snapshot generation to the indexing pipeline for select languages (start with TypeScript/Go?).
3. Implement the `astgrep` helper macro and caching layer.
4. Wire the AST channel into `search` behind a feature flag.
5. Expand language coverage gradually; document resource requirements.

---

## Open Questions
- Storage budget: how much disk per repository are we willing to dedicate to AST snapshots?
- Security: do we need sandboxing for user-provided AST patterns?
- Tooling: should we expose AST matches directly in the CLI/IDE, or keep it macro-only for the LLM?

---

*Revisit this document once we’ve proven the base hybrid search in production and have capacity to ingest/store AST metadata.*
