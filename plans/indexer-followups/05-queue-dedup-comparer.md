# Task 5: Supply Explicit Dedup Comparers for WorkQueue<IndexItem>

## Why
`WorkQueue<T>` supports deduplication but currently relies on `IndexItem` implementing equality semantics implicitly. That makes behavior fragile (e.g., if someone changes `Equals`). Passing a comparer when constructing the queues makes dedup intentions explicit and easier to reason about.

## Plan
1. Define `IndexItemComparer : IEqualityComparer<IndexItem>` that compares `RepoUri` plus the operation type (e.g., hot-path vs. analysis) when necessary.
2. Update `IndexerQueue` and `_analysisQueue` construction to pass the comparer to `WorkQueue`.
3. Ensure `WorkQueue.EnqueueAsync` uses the comparer (verify or add overload if missing).
4. Add tests covering:
   - Duplicate URIs deduped when comparer says so.
   - Different URIs or different operation types not deduped.
5. Update docs/comments in `IndexItem` to note equality is no longer relied upon.

## Pseudocode
```csharp
sealed class IndexItemComparer : IEqualityComparer<IndexItem>
{
    public bool Equals(IndexItem? x, IndexItem? y)
        => x?.Artifact.Uri == y?.Artifact.Uri && x?.Options.Kind == y?.Options.Kind;

    public int GetHashCode(IndexItem item)
        => HashCode.Combine(item.Artifact.Uri, item.Options.Kind);
}

_hotPathQueue = new WorkQueue<IndexItem>(..., new IndexItemComparer());
```

## Definition of Done
- Both hot-path and analysis queues receive explicit comparers.
- Unit tests prove dedup works via comparer and no longer depends on `IndexItem.Equals`.
- Existing functionality unaffected (tests + reindex scenarios pass).
