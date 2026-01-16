# RepoQL Embeddings (Local ONNX)

This folder contains the local, offline embedding stack used by RepoQL.
We ship a compact English embedding model (intfloat/e5-small-v2) and a
minimal tokenizer so semantic search works out-of-the-box with no network access.

## What Ships

- `Embeddings/Model/embedding_model.onnx` — the ONNX-optimized encoder (E5-small-v2)
- `Embeddings/Model/tokenizer.json` — WordPiece vocabulary and config
- `Embeddings/Model/tokenizer_config.json`, `special_tokens_map.json` — metadata for special tokens, normalization

No internet access is required at runtime. Inference runs via ONNX Runtime on CPU
by default; if a compatible GPU is available, ONNX Runtime may use CUDA or DML.

## How It’s Wired

- Provider: `OnnxEmbeddingProvider` (C#)
  - File: `src/tools/RepoQL/src/RepoQL/Embeddings/OnnxEmbeddingProvider.cs`
  - Loads the model + tokenizer from the shipped folder under `AppContext.BaseDirectory`
  - Tokenizes text (WordPiece) to `input_ids` and `attention_mask`
  - Runs the ONNX model, mean‑pools the output, L2‑normalizes to a 384‑dim vector
- DI: configured in `RepoIndexerServiceCollectionExtensions`
  - File: `src/tools/RepoQL/src/RepoQL/RepoIndexerServiceCollectionExtensions.cs`
  - Enabled by default
  - Disable with `REPOQL_EMBED_ENABLED=0`
  - Optional override with `REPOQL_EMBED_MODEL_PATH` (tokenizer must be next to the model)

## Document vs Query Embeddings (Asymmetric Prefixes)

E5 models use asymmetric prefixes for optimal retrieval performance:
- **Passages (documents)**: embeddings are prefixed with `"passage: "` and computed once the initial index scan reaches idle.
  Stored in DuckDB table `document_embedding` (document rows use `node_id = doc_id`, object rows
  capture structured children) as JSON arrays.
- **Queries**: prefixed with `"query: "` and computed on the fly via `embed_query(text)` UDF.
  The primary `search(q, mode := 'auto', ...)` macro uses this UDF.

SQL UDFs:
- `embed_query(text)` — embed text as a search query (prepends "query: " for E5)
- `embed_passage(text)` — embed text as document content (prepends "passage: " for E5)
- `embed_text(text)` — generic embedding without prefix (backwards compatible)

## Tokenization Details (WordPiece)

- Lowercasing and basic token splitting follow the settings in `tokenizer.json`
- WordPiece segmentation produces subword tokens, using `##` prefixes for continuations
- Inputs are clamped to 512 tokens and padded with `[PAD]` as needed
- Special tokens: `[CLS]` is prepended, `[SEP]` appended

## Model Output and Pooling

- If the model returns `[1, T, H]` (last_hidden_state), we mean‑pool over tokens where
  `attention_mask[t] == 1`
- If it returns `[1, H]`, we use that directly
- The final vector is L2‑normalized; dimensionality is fixed at 384 for this model

## DuckDB UDFs and Storage

- `embed_query(text)` → float array for search queries, prepends "query: " (use `::FLOAT[]` to cast)
- `embed_passage(text)` → float array for document content, prepends "passage: " (use `::FLOAT[]` to cast)
- `cosine_similarity_json(a_json, b_json)` → cosine similarity (0..1)
- `document_embedding` stores both document- and object-scope vectors as JSON (VARCHAR) for portability:<br/>
  `(doc_id UUID, node_id UUID, uri TEXT, scope TEXT CHECK(scope IN ('document','object')), model TEXT, dim INT, embedding JSON, updated_at TIMESTAMP)`.<br/>
  Document rows set `node_id = doc_id`; object rows point at structured nodes so search can return precise URIs.

## Failure Modes / Fallbacks

- If the shipped files are missing or ONNX initialization fails, RepoQL falls back
  to a deterministic local hashed embedding provider so search still works.
- The embedding UDFs are null‑safe; queries don’t crash if embeddings are unavailable.

## Telemetry

- No additional ActivitySource was added. Existing spans cover:
  - `RepoQL.Host`: initial embedding refresh span (`repoql.embed.refresh`)
  - `RepoQL.Data.DuckDB`: SQL execution spans for storage and search

## Testing

- The default test suite exercises the end‑to‑end path (in‑memory DuckDB). You can
  sanity check locally:

```
-- After host reaches idle
SELECT COUNT(*) FROM document_embedding;

-- Query-time vector
SELECT embed_text('hello world');

-- Search
SELECT uri, score, semn FROM file_search('docs', 'Find markdown references', k := 5);
```

## Configuration Summary

- Enable/Disable: `REPOQL_EMBED_ENABLED` (default: enabled; set `0` to disable)
- Override model path: `REPOQL_EMBED_MODEL_PATH=/abs/path/embedding_model.onnx`

If you need more models later, mirror this layout next to the binaries and ensure
a matching tokenizer is present.

## Obtaining E5-small-v2 Model Files

To export and optionally quantize the E5-small-v2 model:

```bash
# Install optimum
pip install optimum[onnxruntime]

# Export E5-small-v2 to ONNX
optimum-cli export onnx --model intfloat/e5-small-v2 ./e5-small-v2/

# Optional: Quantize to int8 for smaller size (~33MB)
python -m onnxruntime.quantization.quantize \
    --input ./e5-small-v2/model.onnx \
    --output ./e5-small-v2/embedding_model.onnx \
    --per_channel
```

Copy `embedding_model.onnx` and `tokenizer.json` to the Model folder.
