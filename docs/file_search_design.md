# file_search redesign (keywords + question)

## Why change it?
Agents frequently want to anchor a search on literal symbols (paths, class names, filenames) but also give a free-form question describing the task. The old macro forced them to mash everything into one string, which made the lexical and fuzzy legs noisy and pushed people to overthink prompt craft. Splitting the inputs keeps the macro easy to call while letting each scoring leg focus on the signal it is good at.

## Design constraints
- Default still works with a single `keywords` argument so existing prompts do not explode.
- `question` is optional and only affects the semantic/vector leg.
- Keep the result columns (`doc_id`, `uri`, `bm25n`, `fuzzn`, `semn`, `score`) so downstream projections do not need to change.
- Preserve the current mix of lexical, fuzzy, and semantic scoring, but make the data flow obvious to agents.
- Prefer obvious parameter names and defaults over adding more knobs.

## Macro definition
```sql
CREATE OR REPLACE MACRO file_search(
    keywords VARCHAR,
    question VARCHAR := NULL,
    k INTEGER := 50,
    max_cand INTEGER := 5000,
    bm25_weight DOUBLE := 0.45,
    fuzzy_weight DOUBLE := 0.45,
    semantic_weight DOUBLE := 0.10
) AS TABLE (
    -- body shown in src/RepoQL.Data.DuckDB/Schema/Tables/document_search.sql
);
```
Notes:
- `keywords` should be treated as literal filters (file name fragments, symbol names, repo paths). We continue to use `match_score` and simple substring checks over `document_search.search_key`.
- `question` is optional. When supplied we prepend the recommended BGE query instruction (`Represent this sentence for searching relevant passages: `) before embedding. When omitted we fall back to embedding the keywords string so semantic search still works.
- Weight parameters stay optional; most callers should rely on defaults.

## Execution flow
1. **Lexical scoring**: shortlist documents whose `search_key` contains any keyword tokens. We order by normalized substring hit first, fuzzy match second, shortest URI third. The `keywords` string is lower-cased inside the macro so callers do not have to pre-process.
2. **Semantic scoring**: if `question` is provided we embed that; otherwise we embed `keywords`. The query vector is compared against `document_embedding.embedding` via `cosine_similarity_json`. We cap the candidate list with `max_cand` before normalization so large repos stay fast.
3. **Score fusion**: `combine()` now accepts weights, but we keep current defaults. Each leg is normalized via `zero_one()` so weights are intuitive.
4. **Result set**: we return up to `k` rows ordered by the fused score, breaking ties with URI length. This keeps the interface identical for projections/headlines/etc.

## Agent-facing usage
- "I know the file" → call `file_search('RepoDbGraphStore.cs', question := NULL)`.
- "I know the intent" → call `file_search('', question := 'How do we register DuckDB scalar functions?')` (keywords can be empty; the macro will detect the blank string and lean entirely on semantic/fuzzy legs).
- "Mix of both" → `file_search('document_search.sql repoql', question := 'Why do we normalize bm25 and fuzz scores?')`.
- CLI/query-tool flags: `--keywords` (required, defaults to empty string) and `--question` (optional). When users omit `--question` the CLI should pass `NULL` to avoid double-embedding the same string.

## Example queries we want to enable
1. `file_search('DuckDbGraphStore.cs embedding', question := 'How can we make UDF registration idempotent?')` – surfaces both the file and related docs that mention UDFs.
2. `file_search('Schema/Tables/document_search.sql', question := 'What knobs control the lexical vs semantic weights?')` – returns the schema file plus docs describing scoring.
3. `file_search('AnnotationGraph', question := 'Where do we stitch projections into the search index?')` – finds both code and docs even if the question words never appear verbatim in filenames.

## Implementation checklist
- Rewrite `document_search.sql` with the new macro body, including branching logic to choose `keywords` vs `question` for the query vector.
- Update QueryTool/MCP/CLI call sites so they send both arguments. Provide helpful help-text so agents understand the split inputs.
- Adjust documentation (`docs/file_search_design.md`, CLI help, README) so the two-argument calling convention is visible.
- Add targeted tests around `file_search` consumers (e.g., SearchProjectionTests) to cover keyword-only vs question-only vs both.

## Future considerations
- Allow loaders to contribute extra search metadata columns (e.g., language, format) and extend `file_search` with optional filters once we have real scenarios.
- Consider parameterizing the weights per call when agents have a compelling reason, but keep defaults stable so prompts stay short.
- Once embeddings cover projections (headline/summary/structure), revisit whether they should feed the same table or a sibling `document_projection_embedding` table.
