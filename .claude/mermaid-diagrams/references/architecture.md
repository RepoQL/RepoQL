# Architecture Diagram Reference

**Use for**: Service topology (20+ services), infrastructure dependencies, explicit directional flows, hierarchical grouping

**Don't use for**: Simple trees (<10 nodes - use flowchart), data flows (use Sankey), sequences (use sequence diagram)

---

## Basic Syntax

```mermaid
architecture-beta
    group platform(cloud)[Platform Services]
    group external(internet)[External]

    service identity(server)[Identity] in platform
    service org(server)[Organization] in platform
    service spreedly(cloud)[Spreedly] in external

    identity:R --> L:org
    org:B --> T:spreedly

    %% MEANING: Foundation services with external dependency
    %% DIRECTION: T/B/L/R specifies connection points
```

---

## Groups

```mermaid
architecture-beta
    group foundation(cloud)[Foundation]
    group domain(server)[Domain Services]

    service identity(server)[Identity] in foundation
    service community(server)[Community] in foundation
    service payment(server)[Payment] in domain
    service campaign(server)[Campaign] in domain

    identity:R --> L:payment
    community:R --> L:campaign
```

**Group syntax**: `group id(icon)[Display Name]`

---

## Services

```mermaid
architecture-beta
    service api(server)[API Gateway]
    service db(database)[Database]
    service cache(disk)[Cache]
    service external(internet)[External API]

    api:R --> L:db
    api:B --> T:cache
```

**Service syntax**: `service id(icon)[Display Name] in groupId`

---

## Built-in Icons

| Icon | Use for |
|------|---------|
| `cloud` | Cloud services |
| `database` | Data stores |
| `disk` | Storage/cache |
| `internet` | External systems |
| `server` | Services |

---

## Edge Directions

```mermaid
architecture-beta
    service a(server)[A]
    service b(server)[B]

    a:T --> B:b
    a:B --> T:b
    a:L --> R:b
    a:R --> L:b
```

| Direction | Meaning |
|-----------|---------|
| `T` | Top |
| `B` | Bottom |
| `L` | Left |
| `R` | Right |

**Syntax**: `source:SourceDir --> TargetDir:target`

---

## Group-Level Connections

Connect entire groups:

```mermaid
architecture-beta
    group internal(cloud)[Internal]
    group external(internet)[External]

    service api(server)[API] in internal
    service worker(server)[Worker] in internal
    service vendor(cloud)[Vendor] in external

    internal:R --> L:external
```

Use `{group}` modifier for group-level edges.

---

## Complete Example

```mermaid
architecture-beta
    group foundation(cloud)[Foundation Services]
    group domain(server)[Domain Services]
    group integration(internet)[Integrations]

    service identity(server)[Identity] in foundation
    service organization(server)[Organization] in foundation
    service community(server)[Community] in foundation

    service payment(server)[Payment] in domain
    service campaign(server)[Campaign] in domain
    service statements(server)[Statements] in domain

    service quickbooks(cloud)[QuickBooks] in integration
    service spreedly(cloud)[Spreedly] in integration

    identity:R --> L:payment
    organization:R --> L:campaign
    community:R --> L:statements

    payment:B --> T:spreedly
    statements:B --> T:quickbooks

    %% MEANING: Platform services with external integrations
    %% GROUPS: Foundation (critical), Domain (business), Integration (external)
    %% VALUE: Shows service topology and external boundaries
```

---

## Best Practices

- Use groups for layers/domains/teams
- Explicit direction on all edges
- Declare services before edges
- 10-20 services per diagram max
- Split larger systems into multiple diagrams
- Built-in icons for consistent styling

---

## Common Mistakes

- Edges before declarations (must declare first)
- Missing direction modifiers (`api --> db` fails)
- Too many nodes (>25 unreadable)
- No grouping (loses structure benefit)
- Using for simple topologies (<10 nodes - use flowchart)

---

## Architecture vs Flowchart

| Aspect | Architecture | Flowchart |
|--------|--------------|-----------|
| Grouping | Built-in | Subgraphs |
| Edge direction | Explicit | Implicit |
| Scale | 20+ services | <15 nodes |
| Purpose | Topology | Process |

---

**Note**: Experimental (v11.1.0+) - syntax may evolve.

---

*Architecture diagrams show SERVICE TOPOLOGY with explicit grouping and direction.*
