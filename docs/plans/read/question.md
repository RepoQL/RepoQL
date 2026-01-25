# Plan: question Modifier

Implements: [Design: read-tool.md](../../designs/read-tool.md) — QuestionHandler

## Scope

**Covers:**
- `QuestionHandler` implementing `IModifierHandler`
- Migrate `// question` syntax to `=> question:`
- Reuse existing LLM synthesis logic
- Format answer with citations

**Does not cover:**
- LLM integration (existing `ILlmProvider`, `ExploreOrchestrator`)
- Pattern resolution (handled by dispatcher)

## Enables

- Consistent syntax: `=> question:` like other modifiers
- Existing question functionality preserved

## Prerequisites

- Plan: ModifierDispatcher complete
- Existing `ExecuteWithQuestionAsync` in `ReadOrchestrator`
- Existing `ILlmProvider`

## North Star

Ask a question, get a synthesized answer with citations. Same behavior, unified syntax.

## Done Criteria

### Handler Registration
- The QuestionHandler shall register with modifier name `question`
- The QuestionHandler shall handle `CanHandle("question")` returning true

### Parameter Parsing
- The parameter shall be the question text
- When parameter is empty, return error requesting question

### Execution
- The handler shall delegate to existing LLM synthesis logic
- For small content (<100k tokens), call LLM directly
- For large content, use explore Explain pipeline
- The handler shall format answer with derivation/citations

### Syntax Migration
- The old `// question` syntax shall continue working during transition
- Both syntaxes route to same handler

### Output Format
```
[Answer text synthesized from content]

Derivation:
- file:///src/Auth/TokenService.cs#line=42,48 — token validation logic
- file:///src/Auth/AuthMiddleware.cs#line=15,25 — middleware integration
```

### Budget Handling
- Budget controls answer length
- Citations always included regardless of budget

## Constraints

- **Reuse existing logic**: Delegate to `ExecuteWithQuestionAsync`
- **Backward compatible**: `// question` syntax still works

## References

- [Flow: question.md](../../flows/future/read/question.md)
- [ReadOrchestrator.cs](../../../src/RepoQL.Explore/ReadOrchestrator.cs) — `ExecuteWithQuestionAsync`

## Error Policy

- No question provided: Return "Please provide a question after 'question:'"
- LLM unavailable: Return error suggesting explore Explain as alternative
- No content found: Return error "No content found to answer question"
