# State Diagram Reference

**Use for**: Lifecycle management, status transitions, state machines, workflow states

**Don't use for**: Static structure, one-time processes, linear sequences

---

## Basic Syntax

```mermaid
stateDiagram-v2
    [*] --> Draft

    Draft --> Submitted: Submit
    Submitted --> Approved: Approve
    Submitted --> Rejected: Reject

    Approved --> [*]
    Rejected --> Draft: Revise

    %% MEANING: Document approval workflow
    %% [*] = start and terminal states
```

---

## State Declaration

```mermaid
stateDiagram-v2
    state "Waiting for Review" as WaitingForReview
    state "In Progress" as InProgress

    [*] --> WaitingForReview
    WaitingForReview --> InProgress
```

- Use `state "Display Name" as id` for spaces in names
- Start/end marker: `[*]`

---

## Transitions

```mermaid
stateDiagram-v2
    Active --> Inactive: Deactivate
    Inactive --> Active: Reactivate
    Active --> Deleted: Delete
```

| Syntax | Meaning |
|--------|---------|
| `A --> B` | Transition from A to B |
| `A --> B: Label` | Labeled transition (trigger/action) |
| `[*] --> A` | Initial transition |
| `A --> [*]` | Terminal transition |

---

## Composite States

Nest states for complex hierarchies:

```mermaid
stateDiagram-v2
    [*] --> Active

    state Active {
        [*] --> Running
        Running --> Paused: Pause
        Paused --> Running: Resume
    }

    Active --> Terminated: Stop

    %% MEANING: Active contains sub-states
```

---

## Choice and Fork

```mermaid
stateDiagram-v2
    state CheckResult <<choice>>

    Processing --> CheckResult
    CheckResult --> Success: if valid
    CheckResult --> Failure: if invalid

    state Parallel <<fork>>
    state Join <<join>>

    Start --> Parallel
    Parallel --> TaskA
    Parallel --> TaskB
    TaskA --> Join
    TaskB --> Join
    Join --> End
```

| Marker | Use for |
|--------|---------|
| `<<choice>>` | Decision point |
| `<<fork>>` | Parallel split |
| `<<join>>` | Parallel merge |

---

## Styling States

```mermaid
stateDiagram-v2
    [*] --> Draft
    Draft --> Published
    Draft --> Cancelled

    Published --> Archived
    Archived --> [*]
    Cancelled --> [*]

    classDef success fill:#90EE90,stroke:#2E7D32,color:#000
    classDef error fill:#FFB6C1,stroke:#C62828,color:#000

    class Published,Archived success
    class Cancelled error

    %% SYNTAX: State diagrams use 'class StateName className' not ':::className'
```
*Colors: Green = success states, Red = rejection states*

**Note**: State diagrams use `class StateName className` syntax, not `:::className`.

---

## Notes

```mermaid
stateDiagram-v2
    [*] --> Active

    Active --> Processing
    note right of Processing
        Async job running
        May take up to 5 minutes
    end note

    Processing --> Complete
```

---

## Best Practices

- Show ALL valid transitions (completeness matters)
- Color terminal states (success vs failure)
- Label transitions with trigger events
- Include reverse transitions where valid
- Use composite states for related sub-states
- Mark start `[*] -->` and end `--> [*]` clearly

---

## Common Mistakes

- Missing reverse transitions (can you go back?)
- Incomplete state coverage (missing terminal states)
- Using for one-directional flow (use flowchart)
- No initial state marker
- Missing transition labels (what triggers each transition?)
- Using `:::` instead of `class` for styling

---

## State vs Flowchart

| Characteristic | State Diagram | Flowchart |
|----------------|---------------|-----------|
| Shows | Valid transitions | Process logic |
| Cycles | Common (back-edges) | Uncommon |
| Purpose | What states exist, how to move between them | How to process from start to end |
| Example | Order status lifecycle | Order processing logic |

**Rule**: If entities persist in states and transition between them, use state diagram. If it's a one-shot process, use flowchart.

---

*State diagrams show WHERE things can be and HOW they move between states.*
