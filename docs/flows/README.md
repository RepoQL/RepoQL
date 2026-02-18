# Flows

Process documentation showing how things work end-to-end.

## Structure

```
flows/
├── current/     # How things work today
└── future/      # Target state for planned improvements
```

**current/** documents the system as-built. Use for understanding, debugging, onboarding.

**future/** documents target flows for planned work. These become current when implemented.

## Contents

### Current

| Flow | Description |
|------|-------------|
| [indexing/](current/indexing/) | File discovery → parsing → embedding pipeline (17 documents) |
| [mcp/failure-modes/](current/mcp/failure-modes/) | Detection and diagnosis of MCP client-side failures |
| [host/failure-modes/](current/host/failure-modes/) | Detection and handling of host startup/runtime failures |

### Future

| Flow | Description |
|------|-------------|
| [read/](future/read/) | Read tool modifiers: representation, search, graph, diagnostics, history (19 documents) |
| [operations/](future/operations/) | Operation tracking, ready gating, and progress streaming (4 documents) |
| [llm-service/](future/llm-service/) | gRPC LLM provider service: auth/billing, explain synthesis, reranking, batch embedding, lifecycle, failure modes (7 documents) |
