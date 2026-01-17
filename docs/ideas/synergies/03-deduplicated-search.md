# Synergy 3: Deduplicated Search

> SimHash fingerprinting + Search pipeline = Clone-free results

## Overview

This is the **lowest-effort, highest-leverage** synergy. Repositories are full of duplicates across all content types:

- Copy-pasted code utilities
- Vendored dependencies
- Backup files (*.backup, *.old)
- Generated code and docs
- Similar implementations across modules
- Duplicated documentation sections
- Template-derived configs with minor variations
- Copied examples in different locations

Without deduplication, these clones consume token budget on redundant content. SimHash provides O(1) near-duplicate detection with just 8 bytes per file—works on code, markdown, YAML, and any text content.

**Key principle**: Deduplication affects **token spend**, not **awareness**. Agents still need to know duplicates exist (there may be a reason for AuthServiceV2 or a vendored copy). The solution:

1. **Full content** for canonical/highest-scoring version
2. **Headline only** for detected near-duplicates (awareness without token waste)

This gives situational awareness without redundant content.

## The Problem

```
Query: "parse JSON"

Current results:
1. JsonParser.cs           (original)
2. JsonParser.backup.cs    (backup copy - 99% identical)
3. JsonParser.cs           (in vendor/ - exact copy)
4. JsonParserV2.cs         (refactored - 85% similar)
5. Utils/JsonParser.cs     (copy in different module)

Effective unique results: 2 out of 5
Token waste: 60%
```

## The Solution

```
┌─────────────────────────────────────────────────────────────────┐
│                Deduplication Pipeline                            │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Index Time                                                     │
│   ──────────                                                     │
│   For each file:                                                 │
│     1. Tokenize content (identifiers, keywords, structure)      │
│     2. Compute 64-bit SimHash fingerprint                       │
│     3. Store in artifact.simhash                                │
│                                                                  │
│   Query Time                                                     │
│   ──────────                                                     │
│   1. Run search as normal                                        │
│   2. For each result, check SimHash against already-selected    │
│   3. If Hamming distance ≤ 3: mark as near-duplicate            │
│   4. Return results with duplicate annotations                  │
│                                                                  │
│   Example output:                                                │
│                                                                  │
│   ┌─ FULL CONTENT ──────────────────────────────────────────┐   │
│   │ JsonParser.cs (score=0.92)                              │   │
│   │ public class JsonParser { ... 200 lines ... }           │   │
│   └─────────────────────────────────────────────────────────┘   │
│                                                                  │
│   ┌─ HEADLINE ONLY (near-duplicate, hamming=1) ─────────────┐   │
│   │ JsonParser.backup.cs - backup copy, 99% similar         │   │
│   └─────────────────────────────────────────────────────────┘   │
│                                                                  │
│   ┌─ HEADLINE ONLY (exact duplicate) ───────────────────────┐   │
│   │ vendor/JsonParser.cs - vendored copy, identical         │   │
│   └─────────────────────────────────────────────────────────┘   │
│                                                                  │
│   ┌─ FULL CONTENT ──────────────────────────────────────────┐   │
│   │ JsonParserV2.cs (score=0.85, different implementation)  │   │
│   │ public class JsonParserV2 { ... 180 lines ... }         │   │
│   └─────────────────────────────────────────────────────────┘   │
│                                                                  │
│   Agent knows: 4 files exist, 2 are duplicates                  │
│   Tokens spent: 2 full + 2 headlines (not 4 full)              │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## SimHash Algorithm

**Research**: [SketchingAlgorithms.md](../../research/algorithms/SketchingAlgorithms.md) §4 (SimHash)

```python
def simhash(tokens: List[str], bits: int = 64) -> int:
    """
    Compute SimHash fingerprint.

    Key property: similar documents have similar hashes.
    Hamming distance between hashes ≈ dissimilarity.
    """
    v = [0] * bits

    for token in tokens:
        # Hash token to 64 bits
        h = hash64(token)

        # For each bit position
        for i in range(bits):
            if (h >> i) & 1:
                v[i] += 1   # Bit is 1: vote +1
            else:
                v[i] -= 1   # Bit is 0: vote -1

    # Majority vote for each bit
    fingerprint = 0
    for i in range(bits):
        if v[i] > 0:
            fingerprint |= (1 << i)

    return fingerprint
```

**Why it works**: Each token "votes" for bit values. Similar documents have similar token distributions → similar votes → similar fingerprints.

## Implementation

### Schema Change

```sql
-- Add SimHash column
ALTER TABLE artifact ADD COLUMN simhash UBIGINT;

-- Index for potential future optimizations
CREATE INDEX idx_artifact_simhash ON artifact(simhash);
```

### Compute During Indexing (C#)

```csharp
public class SimHashCalculator
{
    private const int Bits = 64;

    public ulong Compute(string content)
    {
        var tokens = Tokenize(content);
        var votes = new int[Bits];

        foreach (var token in tokens)
        {
            var hash = ComputeHash64(token);
            for (int i = 0; i < Bits; i++)
            {
                votes[i] += ((hash >> i) & 1) == 1 ? 1 : -1;
            }
        }

        ulong fingerprint = 0;
        for (int i = 0; i < Bits; i++)
        {
            if (votes[i] > 0)
                fingerprint |= (1UL << i);
        }

        return fingerprint;
    }

    private IEnumerable<string> Tokenize(string content)
    {
        // Extract: identifiers, keywords, structural tokens
        // Normalize: lowercase, split camelCase
        // Weight: identifiers > keywords > punctuation
        return CodeTokenizer.Tokenize(content);
    }
}
```

### Deduplicated Search Macro

```sql
-- Search with automatic deduplication
CREATE MACRO search_dedup(query, k, hamming_threshold := 3) AS (
    WITH ranked_results AS (
        SELECT
            uri,
            score,
            simhash,
            row_number() OVER (ORDER BY score DESC) as rank
        FROM search(query, k := k * 3)  -- over-retrieve
        JOIN artifact USING (uri)
    ),
    deduplicated AS (
        SELECT
            r1.uri,
            r1.score,
            r1.rank
        FROM ranked_results r1
        WHERE NOT EXISTS (
            -- Check if any higher-ranked result is a near-duplicate
            SELECT 1
            FROM ranked_results r2
            WHERE r2.rank < r1.rank
              AND bit_count(r1.simhash # r2.simhash) <= hamming_threshold
        )
    )
    SELECT uri, score
    FROM deduplicated
    ORDER BY score DESC
    LIMIT k
);

-- Usage
SELECT * FROM search_dedup('parse JSON', 10);
```

### Efficient Deduplication with Window Functions

```sql
-- More efficient: mark duplicates in single pass
CREATE MACRO search_dedup_v2(query, k, threshold := 3) AS (
    WITH results AS (
        SELECT
            uri,
            score,
            simhash,
            row_number() OVER (ORDER BY score DESC) as rank
        FROM search(query, k := k * 3)
        JOIN artifact USING (uri)
    ),
    with_dup_flag AS (
        SELECT
            *,
            -- Check against all previous results (approximation)
            CASE WHEN EXISTS (
                SELECT 1 FROM results r2
                WHERE r2.rank < results.rank
                  AND r2.rank >= results.rank - 10  -- check last 10 only
                  AND bit_count(results.simhash # r2.simhash) <= threshold
            ) THEN true ELSE false END as is_duplicate
        FROM results
    )
    SELECT uri, score
    FROM with_dup_flag
    WHERE NOT is_duplicate
    ORDER BY score DESC
    LIMIT k
);
```

## Synergy with Other Components

### SimHash × PPR

Without SimHash, PPR can expand into duplicates:

```
Seed: AuthService.cs
PPR finds: AuthService.backup.cs (clone!)
           vendor/AuthService.cs (clone!)
           AuthServiceV2.cs (legitimate variant)

With dedup: Only AuthServiceV2.cs kept
PPR budget not wasted on clones
```

### SimHash × MMR

MMR computes pairwise similarity. SimHash provides a **cheap pre-filter**:

```
Before MMR:
  If hamming(a, b) ≤ 3:
    Skip embedding similarity computation (known duplicate)
  Else:
    Compute full cosine similarity

Speedup: ~30% of MMR comparisons avoided
```

### SimHash × Token Budget

Every clone in results wastes tokens:

```
Without dedup: 5 files, 3 are clones
  Token usage: 5000 tokens
  Unique information: ~40%

With dedup: 5 unique files
  Token usage: 5000 tokens
  Unique information: ~95%
```

## Clone Detection API

Beyond search deduplication, expose clone detection as a feature:

```sql
-- Find all near-duplicates of a file
CREATE MACRO find_clones(target_uri, threshold := 3) AS (
    SELECT
        a.uri as clone_uri,
        bit_count(t.simhash # a.simhash) as hamming_distance,
        a.lines as clone_lines
    FROM artifact a
    CROSS JOIN (SELECT simhash FROM artifact WHERE uri = target_uri) t
    WHERE a.uri != target_uri
      AND bit_count(t.simhash # a.simhash) <= threshold
    ORDER BY hamming_distance
);

-- Usage: "What files are copies of this one?"
SELECT * FROM find_clones('file:///src/utils/helpers.ts');
```

```sql
-- Find all clone clusters in repository
CREATE MACRO find_all_clones(threshold := 3) AS (
    WITH clone_pairs AS (
        SELECT
            a1.uri as uri1,
            a2.uri as uri2,
            bit_count(a1.simhash # a2.simhash) as distance
        FROM artifact a1
        JOIN artifact a2 ON a1.uri < a2.uri  -- avoid duplicates
        WHERE bit_count(a1.simhash # a2.simhash) <= threshold
    )
    SELECT * FROM clone_pairs
    ORDER BY distance, uri1
);

-- Returns: pairs of similar files for refactoring review
```

## Threshold Tuning

| Hamming Distance | Similarity | Interpretation |
|------------------|------------|----------------|
| 0 | 100% | Exact duplicate or hash collision |
| 1-2 | 97-99% | Trivial differences (whitespace, comments) |
| 3-4 | 94-97% | Renamed variables, minor edits |
| 5-6 | 90-94% | Moderate changes, still recognizable |
| 7-10 | 80-90% | Significant changes, possibly related |
| >10 | <80% | Different files |

**Recommended threshold**: 3 (catches clones while avoiding false positives)

## Expected Impact

### Quantitative

| Metric | Before | After | Change |
|--------|--------|-------|--------|
| Duplicate rate in results | ~25% | <3% | -88% |
| Token efficiency | 0.75 | 0.95 | +27% |
| PPR expansion waste | ~20% | <5% | -75% |
| Unique topics per result | 4.2 | 5.8 | +38% |

### Storage Cost

| Items | Storage |
|-------|---------|
| 10K files | 80 KB |
| 100K files | 800 KB |
| 1M files | 8 MB |

Negligible compared to embeddings (~1.5 KB per file for 384-dim).

## Performance

| Operation | Time |
|-----------|------|
| Compute SimHash | ~1ms per file |
| Hamming distance | ~10ns (bit_count on XOR) |
| Dedup check (10 comparisons) | ~100ns |

**Conclusion**: Essentially free at query time.

## Dependencies

| Dependency | Status | Notes |
|------------|--------|-------|
| Schema change | Simple | Add one column |
| Indexing hook | Simple | Compute during file processing |
| Search integration | Simple | Macro wrapper |

## Open Questions

1. **Tokenization strategy**: Normalize variable names to catch renamed clones?
2. **Multiple hashes**: Store hashes for different normalizations?
3. **Threshold per language**: Different thresholds for different file types?
4. **Clone groups**: Track canonical version of each clone group?

## References

- [SketchingAlgorithms.md](../../research/algorithms/SketchingAlgorithms.md) - SimHash theory
- [Idea 005](../005-simhash-code-clones.md) - Standalone SimHash implementation
- Charikar (2002) - Similarity estimation techniques
- Manku et al. (2007) - Google's near-duplicate detection

---

*This synergy removes an entire class of noise with 8 bytes per file.*
