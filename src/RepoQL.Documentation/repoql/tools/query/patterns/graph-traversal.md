---
description: "Recursive CTEs for graph traversal, cycle detection, path finding"
tags: ["RecursiveCTE", "CycleDetection", "UsingKey", "GraphTraversal"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Patterns[100%]"]
---

# Graph Traversal Patterns

## Capsule: RecursiveCTE

**Invariant**
Iterate until no new rows are produced using WITH RECURSIVE.

**Example**
```sql
WITH RECURSIVE deps AS (
  SELECT target_id, 1 as depth FROM edge WHERE source_id = @start
  UNION ALL
  SELECT e.target_id, d.depth+1 FROM edge e JOIN deps d ON e.source_id = d.target_id WHERE d.depth < 10
) SELECT * FROM deps
```

**Depth**
- Base case: Non-recursive SELECT seeds iteration
- Recursive case: References CTE name, extends results
- Always include depth limit to prevent infinite loops
- SeeAlso: CycleDetection

---

## Capsule: CycleDetection

**Invariant**
Track visited nodes in a list to prevent infinite loops.

**Example**
```sql
WITH RECURSIVE chain AS (
  SELECT id, [id] as path FROM nodes WHERE id = @start
  UNION ALL
  SELECT n.id, list_append(c.path, n.id) FROM nodes n JOIN chain c ON ... WHERE NOT list_contains(c.path, n.id)
) SELECT * FROM chain
```

**Depth**
- list_contains checks if node already visited
- list_append extends path for next iteration
- Path column useful for debugging routes
- SeeAlso: RecursiveCTE, UsingKey

---

## Capsule: UsingKey

**Invariant**
Optimize recursive CTEs by updating keyed rows instead of accumulating.

**Example**
```sql
WITH RECURSIVE shortest AS (
  SELECT node, 1 as dist FROM edge WHERE source = @start
  UNION ALL USING KEY node
  SELECT e.target, s.dist+1 FROM edge e JOIN shortest s ON ...
) SELECT * FROM shortest
```

**Depth**
- USING KEY: Rows keyed by column, enables updates
- Dramatically reduces memory for graph algorithms
- Best for: shortest path, connected components
- SeeAlso: CycleDetection

---

## Capsule: GraphQueries

**Invariant**
Query code relationships using the edge table with recursive CTEs.

**Example**
```sql
SELECT n.uri FROM edge e JOIN node n ON e.source_node_id = n.id
WHERE e.type = 'CALLS' AND e.destination_node_id = @fn
```

**Depth**
- Edge types: CALLS, IMPORTS, HAS_PART, CONTAINS
- Join with node table for URIs and metadata
- Use span table for source locations
- SeeAlso: RecursiveCTE

---

# Checklist

- [ ] Invariant first, one idea, timeless, ≤30 tokens
- [ ] Example concise, ≤5 lines
- [ ] Depth clarifies with bullets; no history or vendors
