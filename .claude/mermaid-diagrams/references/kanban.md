# Kanban Reference

**Use for**: Workflow state visualization, operational stages, service lifecycle tracking, team ownership

**Don't use for**: Active task tracking (use external tools), complex dependencies, real-time updates

---

## Basic Syntax

```mermaid
kanban
  planning[Planning]
    task1[New API]
  development[Development]
    task2[Auth Migration]
  production[Production]
    task3[Order Service]

    %% MEANING: Deployment pipeline stages
```

---

## Metadata

```mermaid
kanban
  planning[Planning]
    svc1[Payment API]@{assigned: Platform, priority: High}
  development[Development]
    svc2[Identity Migration]@{assigned: Foundation, ticket: PLAT-521}
  staging[Staging]
    svc3[Community Search]@{assigned: Platform, priority: Low}

    %% METADATA: assigned, ticket, priority
```

**Available metadata**:
| Field | Use for |
|-------|---------|
| `assigned` | Team/person ownership |
| `ticket` | External tracking ID |
| `priority` | Very High, High, Low, Very Low |

---

## Ticket Links

```yaml
---
config:
  kanban:
    ticketBaseUrl: 'https://project.atlassian.net/browse/#TICKET#'
---
```

Converts `ticket: ABC-123` into clickable link.

---

## Complete Example

```mermaid
kanban
  backlog[Backlog]
    item1[Rate Limiting]@{assigned: Platform, priority: High}
    item2[Audit Logging]@{assigned: Foundation}
  development[In Development]
    item3[OAuth Refresh]@{assigned: Identity, ticket: AUTH-234}
  review[In Review]
    item4[Cache Layer]@{assigned: Platform, priority: Low}
  production[Production]
    item5[Health Checks]@{assigned: SRE}

    %% MEANING: Service deployment workflow
    %% USE: Operational documentation, not live tracking
```

---

## Best Practices

- Use for **documenting workflow states**, not tracking active work
- Show team ownership with `assigned` field
- Keep 3-5 columns
- Keep 10-15 total tasks max
- Apply metadata consistently (all or none)
- Remember: this is static documentation

---

## Common Mistakes

- Using for active work tracking (diagrams are static)
- Too many cards (>20 cluttered)
- Inconsistent metadata (some cards with fields, some without)
- Treating it as a live board

---

## Syntax Rules

- **Indentation required**: Tasks indented under columns
- **Metadata syntax**: `@{key: value, key: value}`
- **Priority values**: Only four predefined options
- **Static**: Does not update automatically

---

## Kanban vs Other Types

| Need | Use |
|------|-----|
| Document workflow stages | Kanban |
| Show dependencies | Gantt |
| Active sprint tracking | External tools (Jira) |
| State transitions | State diagram |
| Process with decisions | Flowchart |

---

*Kanban diagrams show WORKFLOW STAGES and OWNERSHIP as documentation.*
