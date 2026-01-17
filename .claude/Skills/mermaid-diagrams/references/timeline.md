# Timeline Reference

**Use for**: Historical events, version releases, chronological milestones, evolution documentation

**Don't use for**: Future planning (use Gantt), process flows, dependencies

---

## Basic Syntax

```mermaid
timeline
    title Version History

    2023-01 : Initial Release
    2023-06 : Major Update
    2024-01 : Version 2.0

    %% MEANING: Package evolution over time
```

---

## Multiple Events Per Period

```mermaid
timeline
    title Q1 Releases

    2024-01 : Version 1.0
           : Core functionality
           : Initial documentation

    2024-02 : Version 1.1
           : Bug fixes
           : Performance improvements

    2024-03 : Version 1.2
           : New features
           : Breaking changes

    %% FORMAT: Period : Event : More details
```

---

## Section Grouping

```mermaid
timeline
    title Product Evolution

    section Early Development
        2023-Q1 : Prototype
        2023-Q2 : Alpha release

    section Public Launch
        2023-Q3 : Beta
        2023-Q4 : GA Release

    section Maturity
        2024-Q1 : Enterprise features
        2024-Q2 : Scale improvements
```

---

## Complete Example

```mermaid
timeline
    title Package Evolution

    2023-01 : Initial Release 1.0.0
           : Core functionality
           : Basic documentation

    2023-04 : Version 1.1.0
           : Added async support
           : Performance improvements

    2023-07 : Version 1.2.0
           : Breaking: Removed deprecated API
           : New authentication

    2024-01 : Version 2.0.0
           : Major redesign
           : .NET 8 support

    %% MEANING: Version history with key changes
    %% VALUE: Shows evolution and breaking changes
```

---

## Best Practices

- Consistent time intervals (months, quarters, years)
- Multiple events per period when related
- Highlight breaking changes clearly
- Keep descriptions concise (2-4 words)
- Use sections for phases
- Chronological order (oldest to newest)

---

## Common Mistakes

- Using for future planning (use Gantt)
- Inconsistent time granularity
- Too much detail per event
- No clear progression
- Missing significant milestones

---

## Timeline vs Gantt

| Aspect | Timeline | Gantt |
|--------|----------|-------|
| Purpose | Document history | Plan future |
| Dependencies | No | Yes |
| Duration bars | No | Yes |
| Multiple events | Yes | No |
| Use case | "What happened" | "What will happen" |

---

*Timelines show WHAT happened WHEN in chronological order.*
