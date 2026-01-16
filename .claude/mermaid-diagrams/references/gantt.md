# Gantt Chart Reference

**Use for**: Project schedules, task dependencies, milestone tracking, release planning

**Don't use for**: Historical events (use timeline), process flows (use flowchart)

---

## Basic Syntax

```mermaid
gantt
    title Project Schedule
    dateFormat YYYY-MM-DD

    section Phase 1
    Task A          :a1, 2024-01-15, 7d
    Task B          :a2, after a1, 5d

    section Phase 2
    Task C          :a3, after a2, 10d

    %% MEANING: Project timeline with dependencies
```

---

## Date Formats

| Format | Example |
|--------|---------|
| `YYYY-MM-DD` | 2024-01-15 |
| `DD-MM-YYYY` | 15-01-2024 |
| `YYYY-MM-DD HH:mm` | 2024-01-15 09:00 |

Declare at top: `dateFormat YYYY-MM-DD`

---

## Task Syntax

```
TaskName    :id, startDate, duration
TaskName    :id, after otherId, duration
TaskName    :status, id, startDate, duration
```

**Duration units**: `d` (days), `w` (weeks), `h` (hours)

---

## Task States

```mermaid
gantt
    dateFormat YYYY-MM-DD

    Active task     :active, a1, 2024-01-15, 5d
    Completed       :done, a2, after a1, 3d
    Critical path   :crit, a3, after a2, 2d
    Milestone       :milestone, m1, after a3, 0d
```

| Status | Meaning |
|--------|---------|
| `active` | Currently in progress |
| `done` | Completed |
| `crit` | Critical path (highlighted) |
| `milestone` | Zero-duration marker |

---

## Dependencies

```mermaid
gantt
    dateFormat YYYY-MM-DD

    Design          :des, 2024-01-15, 5d
    Implement       :impl, after des, 10d
    Test            :test, after impl, 5d
    Deploy          :milestone, dep, after test, 0d
```

Use `after taskId` to create dependencies.

---

## Sections

Group related tasks:

```mermaid
gantt
    title Release Plan
    dateFormat YYYY-MM-DD

    section Development
    Core features   :dev1, 2024-01-15, 14d
    API endpoints   :dev2, after dev1, 7d

    section Testing
    Unit tests      :test1, after dev1, 7d
    Integration     :test2, after dev2, 5d

    section Release
    Documentation   :doc, after test2, 3d
    Launch          :milestone, launch, after doc, 0d
```

---

## Excluding Days

```mermaid
gantt
    title Work Schedule
    dateFormat YYYY-MM-DD
    excludes weekends

    Task A  :a1, 2024-01-15, 5d
```

Options: `weekends`, `saturday`, `sunday`, or specific dates.

---

## Complete Example

```mermaid
gantt
    title Package Release Schedule
    dateFormat YYYY-MM-DD

    section Development
    Core implementation       :active, dev, 2024-01-15, 21d
    API endpoints            :api, after dev, 14d
    Error handling           :err, after api, 7d

    section Testing
    Unit tests               :test, after dev, 14d
    Integration tests        :integ, after api, 10d
    Load testing            :load, after integ, 5d

    section Release
    Documentation           :docs, after err, 7d
    Package validation      :crit, valid, after load, 3d
    Publish to NuGet       :milestone, pub, after valid, 0d

    %% MEANING: Full release timeline with dependencies
    %% MARKERS: 'crit' = critical path, 'milestone' = key event
    %% VALUE: Shows parallelizable work and bottlenecks
```

---

## Best Practices

- Mark critical tasks with `crit`
- Use sections to group related work
- Show dependencies with `after taskId`
- Mark milestones with zero duration
- Use realistic durations
- Name tasks descriptively

---

## Common Mistakes

- Using for historical timeline (use timeline diagram)
- No dependencies shown (everything looks parallel)
- Unrealistic durations
- Missing milestones
- Too many tasks (split into phases)
- No critical path identification

---

## Gantt vs Timeline

| Aspect | Gantt | Timeline |
|--------|-------|----------|
| Purpose | Future planning | Past events |
| Dependencies | Yes | No |
| Duration | Yes | No |
| Use case | Project schedule | Version history |

---

*Gantt charts show WHEN things happen and WHAT depends on what.*
