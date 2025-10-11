# Embedded Search Stack Technical Design (revised)

> Goal: one fast, embedded search surface for a coding agent over a local RepoQL database using **DuckDB FTS → C# subsequence UDF → optional semantic re‑rank**, exposed via **table macros**. Core schema stays unchanged: **nodes + edges + spans + annotations + artifact**, with RepoURI as the external locator. The design reuses RepoQL URI UDFs/macros and integrates refresh with the single‑writer lifecycle.

------

## 1. Scope

- **In**: fuzzy file/symbol/document search, command‑palette jumps, diagnostics search, owner lookup; all via SQL.
- **Out**: external servers, background daemons, schema changes to base tables. Extend via **macros/UDFs** only. 

------

## 2. Inputs and Entities (schema alignment)

- **artifact** holds bytes/text; join from nodes to read text. SemType routes parsing. 
- **node** identifies documents and code/doc items; non‑document nodes are addressed by document + span. 
- **edge** encodes composition trees and open‑world references (`REFERS_TO`, `TESTS`, …). 
- **span** stores line/char coordinates; snippets should be preferred for UX. 
- **annotation** is the enrichment layer; consumption via views/macros. 
- **RepoURI** is the canonical locator across all surfaces. 

**Contract**: Do not change base tables; add capabilities by **macros/UDFs**. 

------

## 3. Architecture

**Pipeline**

1. **Candidate generation**: DuckDB **FTS** BM25 over a compact `document_search` table.
2. **Intent ranking**: C# scalar UDF `match_score(pattern, text)` for subsequence matching tuned for paths/titles.
3. **Optional semantic re‑rank**: cosine similarity on embeddings per candidate.
4. **Score mixer**: macro `combine(bm25n, fuzzn, semn)` returns a single score.

**Surface**

- A single macro `file_search(q, k := 50, max_cand := 5000)` returning `(doc_id, uri, bm25n, fuzzn, semn, score)`.
- Additional macros for specialized tasks (e.g., diagnostics, OpenAPI, owner lookup) compose the same building blocks without new tables.
- CLI: extend the existing `xray` command with a search mode; consumers never choose "semantic" explicitly.

------

## 4. Physical Structures

### 4.1 `document_search` table

Materialized from core tables to support FTS and fuzzy scoring.

```sql
CREATE TABLE IF NOT EXISTS document_search AS
SELECT
  n.id                                        AS doc_id,
  n.uri                                       AS uri,
  LOWER(REPLACE(COALESCE(n.uri, ''), '\\', '/'))      AS search_key,
  repository_uri_file_name(n.uri)             AS basename,
  /* prefer a small UDF repository_uri_dir_name(uri); fallback to string ops */
  CASE
    WHEN POSITION('/' IN REVERSE(REPLACE(COALESCE(n.uri,''),'\\','/'))) > 0 THEN
      SUBSTR(REPLACE(COALESCE(n.uri,''),'\\','/'), 1,
             LENGTH(REPLACE(COALESCE(n.uri,''),'\\','/')) - POSITION('/' IN REVERSE(REPLACE(COALESCE(n.uri,''),'\\','/'))))
    ELSE NULL
  END                                         AS dirname
FROM node n
WHERE n.kind = 'document' AND COALESCE(n.uri,'') <> '';
```

> Rationale: leaves core intact, adds searchable projection. 

### 4.2 FTS index

```sql
INSTALL fts; LOAD fts;
PRAGMA create_fts_index('document_search', 'doc_id', 'basename', 'dirname', 'search_key');
```

- Query via `fts_main_document_search.match_bm25(doc_id, $q)` which returns `NULL` if no match. Normalize BM25 with a window.
- Index freshness is managed automatically by the RepoQL single‑writer; no manual rebuild needed.

------

## 5. C# subsequence UDF

### 5.1 API

- Name: `match_score(pattern TEXT, text TEXT) → DOUBLE`
- Pure, vectorized, allocation‑light. Scores subsequence matches with adjacency and boundary bonuses. Good for paths, symbols, and titles.

### 5.2 Implementation sketch

```csharp
using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using DuckDB.NET.Data;

public static class Fuzzy
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsSep(char c) => c=='/' || c=='\\' || c=='_' || c=='-' || c==' ' || c=='.';

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static bool IsBoundary(ReadOnlySpan<char> s, int i)
    {
        if (i == 0) return true;
        char p = s[i - 1], c = s[i];
        return IsSep(p) || (char.IsLower(p) && char.IsUpper(c));
    }

    public static double matchish(ReadOnlySpan<char> pat, ReadOnlySpan<char> txt)
    {
        if (pat.Length == 0) return 1.0;
        if (txt.Length == 0 || pat.Length > txt.Length) return 0.0;

        int[] rented = null;
        Span<int> pos = pat.Length <= 256 ? stackalloc int[pat.Length]
                                          : (rented = ArrayPool<int>.Shared.Rent(pat.Length)).AsSpan(0, pat.Length);

        int j = 0, m = 0;
        for (int i = 0; i < pat.Length; i++)
        {
            char p = char.ToLowerInvariant(pat[i]);
            bool found = false;
            while (j < txt.Length)
            {
                if (p == char.ToLowerInvariant(txt[j])) { pos[m++] = j++; found = true; break; }
                j++;
            }
            if (!found) { if (rented != null) ArrayPool<int>.Shared.Return(rented); return 0.0; }
        }

        double score = 0.0; int prev = -1;
        for (int k = 0; k < m; k++)
        {
            int cur = pos[k];
            double s = 1.0;
            if (prev >= 0) { int gap = cur - prev - 1; s += (gap == 0) ? 1.5 : -Math.Min(gap, 32) * 0.04; }
            if (IsBoundary(txt, cur)) s += 0.8;
            if (cur == 0) s += 0.3;
            score += s; prev = cur;
        }
        score -= Math.Max(0, txt.Length - m) * 0.005;
        if (rented != null) ArrayPool<int>.Shared.Return(rented);
        return Math.Clamp(score / pat.Length, 0.0, 5.0); // higher is better
    }
}

public static class DuckDBFuzzy
{
    public static void Register(DuckDBConnection con)
    {
        con.RegisterScalarFunction<string, string, double>(
            "match_score",
            (readers, writer, rows) =>
            {
                for (ulong i = 0; i < rows; i++)
                {
                    var pat = readers[0].GetValue<string>(i) ?? string.Empty;
                    var txt = readers[1].GetValue<string>(i) ?? string.Empty;
                    writer.WriteValue(Fuzzy.matchish(pat.AsSpan(), txt.AsSpan()), i);
                }
            },
            isPureFunction: true
        );
    }
}
```

**Notes**

- Register once in the writer process before queries.
- Keep `pattern` already normalized in the client (lowercased).
- In RepoQL, `match_score` is registered alongside existing UDFs in `UserDefinedFunctions.RegisterAll()` so macros can rely on it.

### 5.3 Query embedding (Phase 2)

- `embed_text_json(text) -> VARCHAR(JSON)`: if an embedding provider is configured, returns a JSON array for the text embedding; otherwise returns `NULL` quickly (stub). Always registered so `file_search` compiles everywhere.
- OTEL span `repoql.search.embed` captures `{model, dim, provider, duration.ms, ok}` per call.

------

## 6. Macros

### 6.1 Helpers

```sql
CREATE OR REPLACE MACRO combine(bm25n, fuzzn, semn, wb := 0.45, wf := 0.45, ws := 0.10)
AS coalesce(wb*bm25n,0) + coalesce(wf*fuzzn,0) + coalesce(ws*semn,0);

CREATE OR REPLACE MACRO zero_one(x) AS (x / NULLIF(MAX(x) OVER (), 0));
```

### 6.2 VSS capability wrapper (Phase 2)

- `vss_candidates(qvec, top_k)` returns `(doc_id, sem)` from `vss_search(...)` when VSS + embeddings are present; otherwise returns an empty relation (capability wrapper). Always installed so `file_search` never branches.

### 6.3 Main search (single name, both phases)

```sql
CREATE OR REPLACE MACRO file_search(q, k := 50, max_cand := 5000) AS TABLE
WITH cand AS (
  SELECT ds.doc_id, ds.uri,
         fts_main_document_search.match_bm25(doc_id, q) AS bm25
  FROM document_search ds
),
filt AS (
  SELECT * FROM cand WHERE bm25 IS NOT NULL LIMIT max_cand
),
scored AS (
  SELECT f.doc_id, f.uri,
         zero_one(f.bm25)                  AS bm25n,
         match_score(q, ds.search_key)     AS fuzz
  FROM filt f
  JOIN document_search ds USING(doc_id)
),
qv AS (
  SELECT from_json(embed_text_json(q), 'LIST<FLOAT>') AS qvec
),
sem AS (
  SELECT doc_id,
         (sem_raw / NULLIF(MAX(sem_raw) OVER (),0)) AS semn
  FROM (
    SELECT s.doc_id, v.sem AS sem_raw
    FROM scored s
    CROSS JOIN qv
    LEFT JOIN vss_candidates(qv.qvec, max_cand) v USING(doc_id)
  )
)
SELECT s.doc_id, s.uri,
       s.bm25n,
       (s.fuzz / NULLIF(MAX(s.fuzz) OVER (),0)) AS fuzzn,
       sem.semn,
       combine(s.bm25n, (s.fuzz / NULLIF(MAX(s.fuzz) OVER (),0)), sem.semn) AS score
FROM scored s
LEFT JOIN sem USING(doc_id)
WHERE COALESCE(s.fuzz,0) > 0 OR s.bm25n IS NOT NULL
ORDER BY score DESC, LENGTH(s.uri)
LIMIT k;
```

Adds capability via macros only; no base table edits.

------

## 7. Usage

### 7.1 CLI surface (extend xray)

- Extend `repoql xray` with a search mode instead of adding a new command. Examples:
  - `repoql xray --search "user svc controller"` → prints ranked `(uri, score)` in a compact view
  - `repoql xray files --search "config json" --top 25`
  - `repoql xray --search "*.md"` combines fuzz and path heuristics

The CLI delegates to `SELECT * FROM file_search(@query, k := @top)` and pretty‑prints results.

### 7.2 Example SQL

- **RepoURI jump**: `SELECT * FROM file_search('user svc controller');`
- **Diagnostics search** across heterogeneous tools via annotations: rank messages+URIs and surface `resolved_target_uri`.
- **Owner lookup** by fuzzy document URI plus ownership annotations.
- **Command‑palette for Markdown**: fuzzy on headings resolved through composition edges; return `#anchor` targets.

------

## 8. Update and lifecycle (automatic freshness)

- RepoQL’s single‑writer manages search index freshness automatically:
  - During the initial repository index, after the writer goes idle, the host rebuilds `document_search` and recreates the FTS index.
  - On subsequent write batches, the writer marks search state as dirty; when the writer queue goes idle, it refreshes `document_search` and the FTS index in one pass.
  - Readers continue to query; refresh is idempotent and occurs off the hot path.
 - Keep `semantic_key` idempotency for annotations/edges to allow safe re‑ingest (unchanged).

------

## 9. Performance model

- FTS reduces N to **K ≈ 1–5k**.
- UDF cost is **O(∑|text|)** over K candidates; memory‑flat due to stackalloc/ArrayPool.
- Optional semantic term adds **O(K·d)** for embedding dimension `d`.
- Tie‑breakers: `length(uri)`, number of path separators, `annotation` recency if present.

**Tuning**

- Cap `max_cand`.
- Lowercase and fold ASCII in the client.
- `PRAGMA threads` to CPU count.
- Persist `search_key` denormalization in `document_search` to avoid per‑row transforms.

**Implementation alignment with RepoQL**

- UDF placement: `match_score` is registered in `UserDefinedFunctions.RegisterAll()` alongside repo URI helpers; FTS is already enabled by `DuckDbGraphStore.EnableRecommendedExtensions()`.
- Macro creation: macros (e.g., `file_search`, helpers) are installed by `DuckDbGraphStore` similarly to existing `annotations_for` and `xray_*`.
- Refresh cadence: search projection + FTS are refreshed automatically by the single‑writer when it goes idle (post‑initial index and after write batches).

------

## 10. Testing

**UDF**

- Deterministic cases: empty pattern, exact match, spaced matches, case insensitivity.
- Monotonicity: adjacency bonus beats gaps; boundary bonus at separators and camelCase.

**SQL**

- Golden queries for: diagnostics search, owner lookup, heading jump, OpenAPI op jump.
- Validate invariants and referential integrity regularly via provided conformance checks. 

------

## 11. Observability (OpenTelemetry)

Spans
- `repoql.search.request` (attributes: query [sanitized], k, max_cand, features.ft, features.fuzzy, features.sem)
- `repoql.search.fts` (candidate.count, duration.ms)
- `repoql.search.fuzzy` (scored.count, duration.ms)
- `repoql.search.semantic` (present=true/false, re_ranked.count, duration.ms)
- `repoql.search.embed` (model, dim, provider, duration.ms, ok)
- `repoql.search.refresh` (phase=initial|incremental, changed.doc.count, duration.ms)

Metrics
- Counters: `repoql.search.requests`, `repoql.search.errors`, `repoql.search.refresh.count`, `repoql.search.semantic.requests`
- Histograms: `repoql.search.duration.ms`, `repoql.search.fts.candidates`, `repoql.search.fuzzy.duration.ms`, `repoql.search.semantic.duration.ms`, `repoql.search.embed.duration.ms`, `repoql.search.refresh.duration.ms`
- Gauges: `repoql.search.index.state` (0=stale, 1=clean)

Errors set span status and are logged; degraded paths (e.g., no VSS) continue without semantic.

------

## 12. Security and correctness

- RepoURI remains the external locator in all outputs and logs. 
- Use `snippet(uri, ctx)` to minimize byte reads and avoid overfetch. 
- Do not rely on URIs as identity; use `id`. 

------

## 13. Failure modes and fallbacks

- FTS missing: macro returns fuzzy‑only results; log once; `features.ft=false` in spans.
- Embedding provider missing/error: `embed_text_json` returns `NULL`; macro omits semantic; `features.sem=false`.
- VSS missing: `vss_candidates` yields no rows; macro omits semantic; `features.sem=false`.
- Refresh errors: emit error spans; prior index remains in use; retry on next idle.

------

## 14. Migration plan

Phase 1
1. Register `match_score` and install helper macros (`combine`, `zero_one`).
2. Create/refresh `document_search` and FTS index.
3. Add `file_search(q, k, max_cand)` macro.
4. Wire automatic refresh on writer idle (initial + incremental) and OTEL search/refresh spans.
5. Extend `xray --search` to call `file_search`.

Phase 2
1. Add `IEmbeddingProvider` (local CPU model recommended), `document_embedding` table, and VSS index (best‑effort load).
2. Register `embed_text_json` (provider or stub) and `vss_candidates` (real or no‑op).
3. Update `file_search` to compute qvec internally and join `vss_candidates`.
4. Add OTEL embed/VSS spans/metrics.

------

## 15. Future extensions (semantic vectors)

Enabling a semantic channel remains optional and fully embedded:

1) Extension — best effort
- `INSTALL vss; LOAD vss;` in `EnableRecommendedExtensions()`; continue if unavailable.

2) Storage
```sql
CREATE TABLE IF NOT EXISTS document_embedding (
  doc_id    UUID PRIMARY KEY,
  model     TEXT NOT NULL,
  dim       INTEGER NOT NULL,
  embedding FLOAT[384] NOT NULL,
  updated_at TIMESTAMP NOT NULL
);
```
Create an ANN index (HNSW/IVF) when VSS is present.

3) Refresh on idle
- Writer tracks changed documents; on idle, compute embeddings (CPU‑friendly small model), upsert rows, refresh ANN index.

4) Macro integration (intent‑only)
- `file_search` always computes `qvec := from_json(embed_text_json(q),'LIST<FLOAT>')` and `LEFT JOIN vss_candidates(qvec, max_cand)`.
- When capability is absent, `embed_text_json` returns NULL and `vss_candidates` returns empty → BM25+fuzzy only.

5) Providers
- Implement `IEmbeddingProvider` with a small local model (e.g., bge‑small/gte‑small via ONNX Runtime). No changes for consumers.


## 15. Future extensions

- Add table macros: `diagnostics_search(q, k)` composed from the same primitives. 
- Add RepoURI helpers (`repository_uri_*`) for fragment math and JSON Pointer handling in SQL. 

------

## Appendix A — Specialized recipes

### A1. Diagnostics search (annotations)

```sql
WITH diag AS (
  SELECT a.id, a.kind, a.severity, a.message, rt.resolved_target_uri AS uri
  FROM annotation a
  LEFT JOIN annotations_all(NULL, 'hint') rt ON rt.id = a.id
  WHERE a.severity IN ('warning','error')
),
cand AS (
  SELECT *, match_score($q, coalesce(message,'') || ' ' || coalesce(uri,'')) AS fuzz
  FROM diag
)
SELECT uri, kind, severity, message, (fuzz / NULLIF(MAX(fuzz) OVER (),0)) AS fuzzn
FROM cand
WHERE fuzzn > 0
ORDER BY fuzzn DESC
LIMIT 100;
```

Uses the canonical annotations contract and `resolved_target_uri`. 

### A2. Markdown command‑palette

```sql
WITH headings AS (
  SELECT n.id, n.properties->>'text' AS title, d.uri
  FROM node n
  JOIN edge e ON e.destination_node_id = n.id AND e.is_composition = TRUE
  JOIN node d ON d.id = e.source_node_id AND d.kind = 'document'
  WHERE n.kind = 'md_heading'
),
ranked AS (
  SELECT uri || '#anchor=' || title AS target, match_score($q, title) AS fuzz
  FROM headings
)
SELECT target FROM ranked WHERE fuzz > 0 ORDER BY fuzz DESC LIMIT 20;
```

Relies on composition edges from document to heading nodes. 
