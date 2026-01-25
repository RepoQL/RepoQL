# Plan: ModifierDispatcher

Implements: [Design: read-tool.md](../../designs/read-tool.md) — ModifierDispatcher component

## Scope

**Covers:**
- Parse `=> <modifier>: <param>` syntax from read input
- `IModifierHandler` interface definition
- Handler registration and dispatch
- Budget enforcement with repeat-to-confirm caching
- Fall through to existing `ReadOrchestrator` when no modifier

**Does not cover:**
- Individual modifier handlers (separate plans)
- Changes to existing `ReadOrchestrator` progressive disclosure
- Changes to `IReadContentProvider`

## Enables

Once ModifierDispatcher exists:
- All 19 modifier plans can proceed
- Unified syntax `=> modifier:` available for all read operations
- Agents can explicitly control how content is displayed/queried

This is the foundation. All other read modifier plans depend on it.

## Prerequisites

- Existing `ReadOrchestrator` in `RepoQL.Explore`
- Existing `IReadContentProvider` interface
- Existing `ReadDocument` and `ReadExecutionResult` records

## North Star

One entry point, consistent dispatch. No modifier = existing behavior unchanged. With modifier = route to handler. Budget exceeded = cache and confirm. Repeat = return cached result.

## Done Criteria

### Syntax Parsing
- The dispatcher shall parse input matching pattern `<pattern> => <modifier>`
- The dispatcher shall parse input matching pattern `<pattern> => <modifier>: <param>`
- When input contains no `=>`, the dispatcher shall delegate to existing `ReadOrchestrator`
- When modifier is unrecognized, the dispatcher shall return error listing valid modifiers

### Handler Interface
- The `IModifierHandler` interface shall define `ModifierName` property
- The `IModifierHandler` interface shall define `CanHandle(string? modifier)` method
- The `IModifierHandler` interface shall define `ExecuteAsync(documents, parameter, budget, ct)` method
- The `ModifierResult` record shall include `Content`, `TokenCount`, `ExceedsBudget`, `Metadata`

### Handler Registration
- The dispatcher shall discover handlers via dependency injection
- The dispatcher shall match modifier names case-insensitively
- When multiple handlers match, the dispatcher shall use first registered

### Budget Enforcement
- When result `TokenCount` exceeds budget, the dispatcher shall cache the result
- When result exceeds budget, the dispatcher shall return confirmation message with token count
- The cache key shall include pattern, modifier, parameter, and budget
- Cache entries shall expire after 60 seconds

### Repeat-to-Confirm
- When request matches cached key, the dispatcher shall return cached result
- When returning cached result, the footer shall indicate budget was overridden
- The dispatcher shall not re-execute the handler for cached requests

## Constraints

- **No breaking changes**: Existing read behavior without `=>` must work identically
- **Single file**: Implementation in `ReadOrchestrator.cs` or new `ModifierDispatcher.cs` in same project
- **No new dependencies**: Use existing DI patterns from `RepoQL.Explore`

## References

- [Design: read-tool.md](../../designs/read-tool.md) — component diagram, contracts
- [ReadOrchestrator.cs](../../../src/RepoQL.Explore/ReadOrchestrator.cs) — existing implementation to extend
- [Flow: default.md](../../flows/future/read/default.md) — default behavior specification

## Error Policy

- Invalid syntax: Return error with example of correct syntax
- Unknown modifier: Return error listing all registered modifiers
- Handler throws: Catch, return error with message, don't crash
