# Plan: Adaptive Intent

Implements: Explore rendering improvements — allocation curve adapts to result count

## Scope

**Covers:**
- Automatic curve shift when result count is low relative to budget
- Intent modifier adjustment in `ValueBasedAllocator`
- MaxChildren scaling based on effective intent

**Does not cover:**
- Changes to search behavior (search still uses the declared intent)
- Changes to intent semantics (Inventory/Locate/Inspect remain distinct)
- New intent types

## Enables

Once Adaptive Intent exists:
- **Small result sets get depth** — 2 results with a Locate budget of 2000 tokens get Inspect-level detail instead of wasting budget on breadth
- **Budget fully utilized** — no more "asked for 2000 tokens, got 800 because Locate curve spread too thin across 3 results"
- **Natural behavior** — agents don't need to guess the right intent; Locate auto-escalates when appropriate

## Prerequisites

- `ValueBasedAllocator.Allocate` receives both `results` count and `tokenBudget` (already does)
- Intent modifier logic exists in `GetIntentModifier` and `GetMaxChildrenForIntent` (already does)

## North Star

The agent picks intent based on what they *know*, not what they expect to *find*. The allocator adapts to what was actually found.

## Done Criteria

### Curve Adaptation

- When result count is 3 or fewer and intent is Inventory, the allocator shall use Locate's allocation curve
- When result count is 3 or fewer and intent is Locate, the allocator shall use Inspect's allocation curve
- When result count exceeds 3, the allocator shall use the declared intent's curve unchanged
- The threshold (3) shall be a named constant, not a magic number

### Budget Utilization

- When adaptation triggers, the allocated tokens shall be within 90% of the requested budget
- The adaptation shall not cause tokens to exceed the requested budget

### Children Scaling

- When adaptation triggers, `GetMaxChildrenForIntent` shall use the adapted intent, not the declared intent
- When Inventory adapts to Locate, max children shall increase from 3 to 5
- When Locate adapts to Inspect, max children shall increase from 5 to 8

## Constraints

- **Search unaffected** — adaptation happens in the allocator only, after search is complete
- **Declared intent preserved** — the `intent` parameter in output/footer still reflects what the agent asked for
- **One step only** — Inventory can shift to Locate behavior, never directly to Inspect

## References

- `src/RepoQL.Explore/ValueBasedAllocator.cs:17` — `Allocate` entry point
- `src/RepoQL.Explore/ValueBasedAllocator.cs:70` — `GetIntentModifier`
- `src/RepoQL.Explore/ValueBasedAllocator.cs:83` — `GetMaxChildrenForIntent`

## Error Policy

No error cases — this is a pure optimization in the allocation path.
