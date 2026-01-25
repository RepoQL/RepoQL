# Plan: content Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — ContentHandler

## Scope

**Covers:**
- `ContentHandler` implementing `IModifierHandler`
- Force full content representation for all matched files
- Uniform representation (all files as full content)
- Budget confirmation when content exceeds budget

**Does not cover:**
- Content retrieval (existing in `IReadContentProvider`)
- Pattern resolution (handled by dispatcher)

## Enables

- Agents can explicitly request full content
- See actual code when needed for modifications

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `ReadDocument.TextContent` populated

## North Star

Every matched file rendered as full source with line numbers. All or nothing.

## Done Criteria

### Handler Registration
- The ContentHandler shall register with modifier name `content`
- The ContentHandler shall handle `CanHandle("content")` returning true

### Execution
- The handler shall extract `TextContent` from each `ReadDocument`
- The handler shall prepend file URI header to each file's content
- The handler shall include line numbers
- When a document has no content, the handler shall show URI with error
- The handler shall calculate total token count of output

### Budget Handling
- When total tokens exceed budget, the handler shall set `ExceedsBudget = true`
- The result shall include file count and total lines in metadata

### Output Format
```
--- file:///path/to/file.cs ---
  1: using System;
  2:
  3: namespace Example
  4: {
  5:     public class Foo
...
```

## Constraints

- **Uniform representation**: All files get full content
- **No truncation**: Either all fit or confirm, never partial without consent
- **Binary files**: Show indicator instead of content

## References

- [Flow: content.md](../../flows/future/read/content.md)

## Error Policy

- Binary file: Show `(binary file, N bytes)` instead of content
- Unreadable file: Show URI with error message
- Continue processing all files
