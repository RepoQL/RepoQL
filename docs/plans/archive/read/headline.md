# Plan: headline Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — HeadlineHandler

## Scope

**Covers:**
- `HeadlineHandler` implementing `IModifierHandler`
- Force headline representation for all matched files
- Uniform representation (all files as headlines)
- Budget confirmation when headlines exceed budget

**Does not cover:**
- Headline generation (existing in X-Ray)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can explicitly request headline-only view
- Scan many files quickly (~500 tokens for 100 files)

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `ReadDocument.Headline` populated by X-Ray

## North Star

Every matched file rendered as single-line headline. All or nothing—either all headlines fit, or confirmation requested.

## Done Criteria

### Handler Registration
- The HeadlineHandler shall register with modifier name `headline`
- The HeadlineHandler shall handle `CanHandle("headline")` returning true

### Execution
- The handler shall extract `Headline` from each `ReadDocument`
- The handler shall concatenate headlines with file URIs
- When a document has no headline, the handler shall show URI with placeholder
- The handler shall calculate total token count of output

### Budget Handling
- When total tokens exceed budget, the handler shall set `ExceedsBudget = true`
- The result shall include file count in metadata

### Output Format
```
file:///path/to/file.cs | ClassName : IInterface | Method1, Method2 | 150 ln, ~800 tok
file:///path/to/other.cs | OtherClass | Method3 | 80 ln, ~400 tok
```

## Constraints

- **Uniform representation**: All files get headlines, not mixed levels
- **No truncation**: Either all fit or confirm, never partial without consent

## References

- [Flow: headline.md](../../flows/future/read/headline.md)
- [North Star: xray-elements.md](../../north-star/xray-elements.md) — headline format spec

## Error Policy

- Missing headline: Show URI with `(no headline available)`
- Continue processing all files even if some lack headlines
