---
description: "Fuzzy string matching, full-text search with ranking, stemming"
tags: ["FuzzyMatch", "FullTextSearch", "Stemming", "JaroWinkler", "Levenshtein"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Patterns[100%]"]
---

# Text Analysis Patterns

## Capsule: FuzzyMatch

**Invariant**
Score string similarity using edit distance or phonetic algorithms.

**Example**
```sql
SELECT name, jaro_winkler_similarity('IndexEngine', name) as score
FROM Functions WHERE score > 0.8
```

**Depth**
- jaro_winkler: 0-1 score, favors prefix matches
- levenshtein: Count of edits needed
- damerau_levenshtein: Adds transposition as single edit
- NotThis: All are case-sensitive; use lower() if needed

---

## Capsule: FullTextSearch

**Invariant**
Create inverted index for relevance-ranked keyword search.

**Example**
```sql
PRAGMA create_fts_index('docs', 'id', 'content', stemmer := 'porter');
SELECT *, fts_main_docs.match_bm25(id, 'query') as score FROM docs;
```

**Depth**
- BM25: Industry-standard relevance ranking
- Stemmer options: porter, english, german, french (25+ languages)
- NotThis: Index does not auto-update; rebuild after changes
- SeeAlso: FuzzyMatch

---

## Capsule: Stemming

**Invariant**
Reduce words to base form for normalized matching.

**Example**
```sql
SELECT stem('running', 'porter') -- returns 'run'
```

**Depth**
- Stemmers: porter (English), snowball variants for others
- NotThis: Stemming is lossy; different words may stem same
- SeeAlso: FullTextSearch

---

# Checklist

- [ ] Invariant first, one idea, timeless, ≤30 tokens
- [ ] Example concise, ≤5 lines
- [ ] Depth clarifies with bullets; no history or vendors
