# Plan: structure Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — StructureHandler

## Scope

**Covers:**
- `StructureHandler` implementing `IModifierHandler`
- Force structure representation for all matched files
- Uniform representation (all files as structure)
- Budget confirmation when structures exceed budget

**Does not cover:**
- Structure generation (existing in X-Ray)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can explicitly request structure-only view
- Navigate to symbols without reading full content

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `ReadDocument.Structure` populated by X-Ray

## North Star

Every matched file rendered as hierarchical outline with signatures and URI fragments. All or nothing.

## Done Criteria

### Handler Registration
- The StructureHandler shall register with modifier name `structure`
- The StructureHandler shall handle `CanHandle("structure")` returning true

### Execution
- The handler shall extract `Headline` and `Structure` from each `ReadDocument`
- The handler shall format as headline followed by structure
- When a document has no structure, the handler shall show headline only with note
- The handler shall calculate total token count of output

### Budget Handling
- When total tokens exceed budget, the handler shall set `ExceedsBudget = true`
- The result shall include file count in metadata

### Output Format
```
file:///path/to/file.cs
ClassName : IInterface | Method1, Method2 | 150 ln
  +class ClassName : IInterface
    +void Method1(string arg)    #symbol=Method1
    +int Method2()               #symbol=Method2
    -bool ValidateInternal()     #line=45,60
```

## Constraints

- **Uniform representation**: All files get structure, not mixed levels
- **No truncation**: Either all fit or confirm, never partial without consent

## References

- [Flow: structure.md](../../flows/future/read/structure.md)
- [North Star: xray-elements.md](../../north-star/xray-elements.md) — structure format spec

## Error Policy

- Missing structure: Show headline with `(structure not available for this format)`
- Continue processing all files
