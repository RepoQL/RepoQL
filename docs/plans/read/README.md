# Read Tool Implementation Plans

Plans for implementing the read tool modifier system.

## Design Reference

- [Design: read-tool.md](../../designs/read-tool.md)
- [North Star: read-tool.md](../../north-star/read-tool.md)
- [Flows: read/](../../flows/future/read/)

## Implementation Order

| # | Plan | Status | Complexity |
|---|------|--------|------------|
| 1 | modifier-dispatcher | Done | Medium |
| 2 | [headline](headline.md) | Pending | Trivial |
| 3 | [structure](structure.md) | Pending | Trivial |
| 4 | [content](content.md) | Pending | Trivial |
| 5 | [tree](tree.md) | Pending | Easy |
| 6 | [lint](lint.md) | Pending | Easy |
| 7 | [history](history.md) | Pending | Easy |
| 8 | [changes](changes.md) | Pending | Easy |
| 9 | [blame](blame.md) | Pending | Easy |
| 10 | [question](question.md) | Pending | Easy |
| 11 | [grep](grep.md) | Pending | Easy |
| 12 | [regex](regex.md) | Pending | Easy |
| 13 | [similar](similar.md) | Pending | Medium |
| 14 | [docs](docs-modifier.md) | Pending | Medium |
| 15 | [find](find.md) | Pending | Medium |
| 16 | [edges](edges.md) | Pending | Medium |
| 17 | [roots](roots.md) | Pending | Medium |
| 18 | [leaves](leaves.md) | Pending | Medium |
| 19 | [tests](tests.md) | Pending | Medium |
| 20 | [astgrep](astgrep.md) | Pending | Hard |

## Dependency Graph

```
modifier-dispatcher
    └── all other plans depend on this
```

All modifier plans share the same prerequisite: ModifierDispatcher must be complete.

## Lifecycle

Plans are deleted when implemented. Update status column as work progresses.
