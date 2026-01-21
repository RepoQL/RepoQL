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
| `host-client-architecture.md` | MCP client ↔ gRPC host connection lifecycle |
| `indexing.md` | File discovery → parsing → embedding pipeline |

### Future

| Flow | Description |
|------|-------------|
| `mcp/failure-modes/` | Detection and diagnosis of MCP client-side failures |
| `host/failure-modes/` | Detection and handling of host startup/runtime failures |
