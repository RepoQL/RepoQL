# Mindmap Reference

**Use for**: Concept hierarchies, brainstorming capture, topic breakdowns, knowledge organization

**Don't use for**: Workflows (use flowchart), code structure (use class diagram), time-based (use timeline)

---

## Basic Syntax

```mermaid
mindmap
    root((Central Topic))
        Branch A
            Leaf 1
            Leaf 2
        Branch B
            Leaf 3
            Leaf 4

    %% ROOT: Double parentheses for root node
```

---

## Node Shapes

```mermaid
mindmap
    root((Circle Root))
        [Square]
        (Rounded)
        ))Cloud((
        {{Hexagon}}
```

| Syntax | Shape |
|--------|-------|
| `((text))` | Circle |
| `[text]` | Square |
| `(text)` | Rounded |
| `))text((` | Cloud |
| `{{text}}` | Hexagon |

---

## Hierarchy Structure

```mermaid
mindmap
    root((Package Design))
        API Surface
            Public Methods
            Extension Methods
            Interfaces
        Testing
            Unit Tests
            Integration Tests
            Performance Tests
        Documentation
            README
            API Docs
        Dependencies
            Internal Packages
            External Packages

    %% MEANING: Hierarchical breakdown of considerations
    %% DEPTH: 3 levels (root -> branch -> leaf)
```

---

## Icons

```mermaid
mindmap
    root((System))
        ::icon(fa fa-server)
        Services
            ::icon(fa fa-cog)
            API
            Workers
        Storage
            ::icon(fa fa-database)
            Database
            Cache
```

Uses Font Awesome icons: `::icon(fa fa-iconname)`

---

## Best Practices

- Keep to 3-4 levels deep (root -> branch -> sub -> leaf)
- Balance branches (similar depth and breadth)
- Root node in `((double parentheses))`
- Use for brainstorming capture
- One concept per node
- Clear, concise labels

---

## Common Mistakes

- Using for sequential workflows (use flowchart)
- Unbalanced depth (one branch with 5 levels, another with 2)
- Too many levels (>5 becomes unreadable)
- Too many nodes per branch
- Unclear hierarchy
- Using for processes with order

---

## Mindmap vs Other Types

| Need | Use |
|------|-----|
| Concept breakdown | Mindmap |
| Process with decisions | Flowchart |
| Class structure | Class diagram |
| Data relationships | ER diagram |
| Service topology | Architecture |

---

*Mindmaps show HOW concepts relate hierarchically.*
