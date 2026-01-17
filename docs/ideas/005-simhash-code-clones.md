# SimHash for Code Clone Detection

> Detect near-duplicate code using locality-sensitive hashing

## Problem

Code repositories often contain:
- Copy-pasted code with minor modifications
- Generated code that's structurally identical
- Vendored dependencies duplicated across modules

Detecting these clones helps:
- Reduce redundant search results
- Identify refactoring opportunities
- Flag suspicious duplication

Currently no efficient way to find "similar but not identical" files.

## Proposed Solution

Use **SimHash** (Charikar's random hyperplane LSH) to fingerprint code files, enabling O(1) near-duplicate lookup.

```
┌─────────────────────────────────────────────────────────────────┐
│                  SimHash Pipeline                                │
├─────────────────────────────────────────────────────────────────┤
│                                                                  │
│   Code File                                                      │
│       │                                                          │
│       ▼                                                          │
│   ┌──────────────┐                                               │
│   │ Tokenize &   │  → ["function", "auth", "validate", ...]      │
│   │ Extract      │     (identifiers, keywords, structure)        │
│   └──────────────┘                                               │
│       │                                                          │
│       ▼                                                          │
│   ┌──────────────┐                                               │
│   │ Hash each    │  → [0x3f2a..., 0x8c1b..., ...]               │
│   │ token        │                                               │
│   └──────────────┘                                               │
│       │                                                          │
│       ▼                                                          │
│   ┌──────────────┐                                               │
│   │ Aggregate    │  → 64-bit SimHash fingerprint                 │
│   │ (weighted)   │     0xA3F2891C4B2E7D01                        │
│   └──────────────┘                                               │
│                                                                  │
│   Similarity: Hamming distance < threshold → likely clone        │
│                                                                  │
└─────────────────────────────────────────────────────────────────┘
```

## Algorithm

```python
def simhash(tokens: List[str], bits: int = 64) -> int:
    """Compute SimHash fingerprint for a token sequence."""
    v = [0] * bits

    for token in tokens:
        # Hash token to bits
        h = hash_to_bits(token, bits)

        for i in range(bits):
            if h & (1 << i):
                v[i] += 1  # Bit is 1: increment
            else:
                v[i] -= 1  # Bit is 0: decrement

    # Convert to fingerprint
    fingerprint = 0
    for i in range(bits):
        if v[i] > 0:
            fingerprint |= (1 << i)

    return fingerprint

def hamming_distance(a: int, b: int) -> int:
    """Count differing bits."""
    return bin(a ^ b).count('1')

def is_near_duplicate(fp1: int, fp2: int, threshold: int = 3) -> bool:
    """Check if two fingerprints indicate near-duplicates."""
    return hamming_distance(fp1, fp2) <= threshold
```

## Implementation in RepoQL

### Schema Addition

```sql
ALTER TABLE artifact ADD COLUMN simhash UBIGINT;

-- Index for fast lookup (optional: band-based LSH index)
CREATE INDEX idx_simhash ON artifact(simhash);
```

### Computing SimHash During Indexing

```csharp
// In file processor
public class SimHashCalculator
{
    private const int Bits = 64;

    public ulong Compute(IEnumerable<string> tokens)
    {
        var v = new int[Bits];

        foreach (var token in tokens)
        {
            var hash = ComputeHash(token);
            for (int i = 0; i < Bits; i++)
            {
                v[i] += ((hash >> i) & 1) == 1 ? 1 : -1;
            }
        }

        ulong fingerprint = 0;
        for (int i = 0; i < Bits; i++)
        {
            if (v[i] > 0) fingerprint |= (1UL << i);
        }
        return fingerprint;
    }
}
```

### Query: Find Clones

```sql
-- Find near-duplicates of a specific file
-- Using DuckDB's bit_count for Hamming distance
SELECT
    a2.uri,
    bit_count(a1.simhash # a2.simhash) as hamming_dist  -- XOR then popcount
FROM artifact a1, artifact a2
WHERE a1.uri = 'file:///src/auth/validator.cs'
  AND a1.uri != a2.uri
  AND bit_count(a1.simhash # a2.simhash) <= 5  -- threshold
ORDER BY hamming_dist;
```

### Macro for Clone Detection

```sql
CREATE MACRO find_clones(uri, threshold := 5) AS (
    SELECT
        a2.uri as clone_uri,
        bit_count(a1.simhash # a2.simhash) as distance
    FROM artifact a1, artifact a2
    WHERE a1.uri = uri
      AND a1.uri != a2.uri
      AND bit_count(a1.simhash # a2.simhash) <= threshold
    ORDER BY distance
);

-- Usage
SELECT * FROM find_clones('file:///src/utils/helpers.ts');
```

## Token Extraction Strategy

For code, extract:

| Token Type | Example | Weight |
|------------|---------|--------|
| Identifiers | `validateToken`, `userId` | 1.0 |
| Keywords | `function`, `class`, `if` | 0.5 |
| Structural | `{`, `}`, `=>` | 0.3 |
| Literals | String/number constants | 0.2 |

**Normalization** (to catch renamed clones):
- Normalize variable names: `userId` → `VAR1`
- Normalize string literals: `"error"` → `STRING`
- Keep structure and keywords

## Expected Benefits

| Use Case | Benefit |
|----------|---------|
| Search deduplication | Don't show 5 copies of same file |
| Refactoring hints | "These 3 files are nearly identical" |
| Copy-paste detection | Flag suspicious duplication |
| Vendored code detection | Identify third-party code |

## Threshold Tuning

| Hamming Distance | Interpretation |
|------------------|----------------|
| 0 | Exact duplicate (or hash collision) |
| 1-3 | Very similar (renamed variables) |
| 4-6 | Similar structure, some changes |
| 7-10 | Related but distinct |
| >10 | Unrelated |

## Complexity

- **Compute**: O(n) where n = tokens in file
- **Storage**: 8 bytes per file (64-bit fingerprint)
- **Query**: O(N) naive scan, O(1) with LSH bands

## Open Questions

1. Should we store multiple SimHash variants (different tokenizations)?
2. Use MinHash instead for set-based similarity?
3. Expose clone detection in xray output?

## References

- [SketchingAlgorithms.md](../research/algorithms/SketchingAlgorithms.md) - SimHash theory
- Charikar (2002) - Similarity estimation techniques
- Manku et al. (2007) - Detecting near-duplicates for web crawling (Google)
