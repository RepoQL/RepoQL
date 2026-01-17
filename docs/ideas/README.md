# RepoQL Ideas

> Actionable improvement ideas derived from algorithm research

## Overview

These documents capture concrete ideas for improving RepoQL's search and retrieval capabilities. Each idea includes problem statement, proposed solution, implementation sketch, and expected impact.

## Ideas by Priority

### High Priority (Core Value Propositions)

| ID | Idea | Impact | Complexity |
|----|------|--------|------------|
| [001](001-mmr-diversity-selection.md) | MMR Diversity Selection | High | Medium |
| [002](002-ppr-context-expansion.md) | PPR Context Expansion | High | Medium |

These directly address RepoQL's core value: **maximum insight, minimum tokens**.

### Medium Priority (Quality Improvements)

| ID | Idea | Impact | Complexity |
|----|------|--------|------------|
| [003](003-code-query-expansion.md) | Code Query Expansion | Medium | Low |
| [004](004-graph-ranking-features.md) | Graph Ranking Features | Medium | Medium |
| [007](007-bm25-parameter-tuning.md) | BM25 Parameter Tuning | Medium | Low |
| [008](008-entropy-diversity-scoring.md) | Entropy Diversity Scoring | Medium | Medium |

These improve search quality with reasonable implementation effort.

### Lower Priority (Advanced Capabilities)

| ID | Idea | Impact | Complexity |
|----|------|--------|------------|
| [005](005-simhash-code-clones.md) | SimHash Code Clones | Medium | Low |
| [006](006-spectral-module-detection.md) | Spectral Module Detection | Medium | High |

These add new capabilities beyond core search.

## Recommended Implementation Order

```
Phase 1: Quick Wins (Low complexity, immediate value)
├── 003-code-query-expansion     [Recall improvement]
├── 007-bm25-parameter-tuning    [Ranking improvement]
└── 005-simhash-code-clones      [New capability: duplicate detection]

Phase 2: Diversity & Coverage
├── 001-mmr-diversity-selection  [Token efficiency]
└── 008-entropy-diversity-scoring [Information-theoretic diversity]

Phase 3: Graph Intelligence
├── 002-ppr-context-expansion    [Structural context]
├── 004-graph-ranking-features   [Graph signals in ranking]
└── 006-spectral-module-detection [Module discovery]
```

## Research Foundation

These ideas are derived from comprehensive research in:

### Applied Research
| Research Doc | Ideas Derived |
|--------------|---------------|
| [BudgetedSelection.md](../research/algorithms/BudgetedSelection.md) | 001 (MMR) |
| [GraphRanking.md](../research/algorithms/GraphRanking.md) | 002 (PPR), 004 (Centrality) |
| [QueryExpansion.md](../research/algorithms/QueryExpansion.md) | 003 (Code expansion) |
| [TwoStageRanking.md](../research/algorithms/TwoStageRanking.md) | 004 (Feature engineering) |
| [HybridRetrieval.md](../research/algorithms/HybridRetrieval.md) | RRF patterns in all |

### Mathematical Foundations
| Research Doc | Ideas Derived |
|--------------|---------------|
| [InformationTheory.md](../research/algorithms/InformationTheory.md) | 008 (Entropy diversity) |
| [SpectralGraphTheory.md](../research/algorithms/SpectralGraphTheory.md) | 006 (Module detection) |
| [ProbabilisticRetrieval.md](../research/algorithms/ProbabilisticRetrieval.md) | 007 (BM25 tuning) |
| [SketchingAlgorithms.md](../research/algorithms/SketchingAlgorithms.md) | 005 (SimHash clones) |

## Success Metrics

| Idea | Primary Metric | Target |
|------|----------------|--------|
| 001 | Topic coverage in top-10 | +30% distinct topics |
| 002 | Related file discovery | +25% recall of related files |
| 003 | Zero-result query rate | -50% |
| 004 | MRR for navigation queries | +15% |
| 005 | Clone detection precision | >90% at 3-bit Hamming threshold |
| 006 | Module coherence (intra-cluster edge ratio) | >0.7 |
| 007 | MRR on code search benchmark | +10% |
| 008 | Information coverage (entropy) | +20% vs naive top-k |

## Adding New Ideas

When adding a new idea document:

1. Use sequential numbering: `005-descriptive-name.md`
2. Include: Problem, Solution, Implementation Sketch, Expected Impact
3. Reference source research document
4. Add to this README's priority table
