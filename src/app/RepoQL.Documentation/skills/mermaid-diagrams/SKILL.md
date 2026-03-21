---
description: "Effective Mermaid diagrams. Use when creating markdown documents with human audiences."
tags: ["skill", "mermaid-diagrams", "diagrams", "visualization", "markdown"]
audience: ["LLMs"]
categories: ["Skill[100%]"]
---

# Mermaid Diagrams

Diagrams reveal relationships that prose cannot express efficiently. Before creating one, ask if a list or table would work.

## The Cardinal Rule

**Never diagram linear sequences.**

```
A --> B --> C --> D
```

This is a list pretending to be a diagram. Use a numbered list instead.

**Test**: If there are no branches, decisions, or parallel paths, use a list.

---

## Capsule: DiagramValue

**Invariant**
A diagram earns its place only when it reveals relationships or patterns that would require paragraphs to explain.

**Example**
Request flows through validation, cache check, and database lookup with error handling at each branch. A flowchart shows all paths instantly; prose would need three paragraphs.
//BOUNDARY: If deleting the diagram loses no critical information, delete it.

**Depth**
- Good diagrams: reveal patterns, show multiple dimensions, enable instant recognition
- Bad diagrams: waste tokens, add complexity without clarity, look impressive but convey little
- Test: Can you explain it faster with a list or table? If yes, use those.

---

## Capsule: SyntaxSafety

**Invariant**
Quote labels containing spaces or special characters; escape markdown syntax in labels.

**Example**
```mermaid
graph LR
    A["User Request"] --> B{Size?}
    B -->|"> 1MB"| C["Process (async)"]
    B -->|"< 1MB"| D["Process (sync)"]
```
//BOUNDARY: Unquoted labels with spaces break rendering.

**Depth**
- Requires quotes: spaces, parentheses, brackets, colons, commas
- Requires escaping: `>`, `<`, `-` at start, `1.` at start
- Never use list numbering (`1. 2. 3.`) in labels - breaks Mermaid syntax
- `\n` renders literally — use `<br/>` for line breaks
- SeeAlso: `help:///skills/mermaid-diagrams/syntax.md`

---

## Capsule: MeaningComments

**Invariant**
Document diagram meaning for both Claude (in `%%` comments) and users (in visible text or legend).

**Example**
```mermaid
graph TD
    Request --> Check{Valid?}
    Check -->|Yes| Success:::success
    Check -->|No| Error:::error

    classDef success fill:#90EE90,stroke:#2E7D32,color:#000
    classDef error fill:#FFB6C1,stroke:#C62828,color:#000

    %% MEANING: Request validation flow
    %% GOTCHA: Retries omitted for clarity
```
*Colors: Green = success path, Red = error path*

**Depth**
- `%%` comments: For Claude's context (invisible in rendered output)
- Markdown text below diagram: For users viewing rendered output
- Both are needed when colors carry meaning
- Standard comments: MEANING, COLOR, GOTCHA, TIMING, NAVIGATION

---

## Choosing the Right Diagram

| Need | Use | Reference |
|------|-----|-----------|
| Process with branches/decisions | Flowchart | `help:///skills/mermaid-diagrams/flowchart.md` |
| Multi-party interactions over time | Sequence | `help:///skills/mermaid-diagrams/sequence.md` |
| State transitions | State diagram | `help:///skills/mermaid-diagrams/state.md` |
| Database schema | ER diagram | `help:///skills/mermaid-diagrams/er-diagram.md` |
| Code class hierarchy | Class diagram | `help:///skills/mermaid-diagrams/class.md` |
| Project schedule | Gantt chart | `help:///skills/mermaid-diagrams/gantt.md` |
| Historical events | Timeline | `help:///skills/mermaid-diagrams/timeline.md` |
| Concept hierarchy | Mindmap | `help:///skills/mermaid-diagrams/mindmap.md` |
| Quantity flows | Sankey | `help:///skills/mermaid-diagrams/sankey.md` |
| Proportions (3-7 items) | Pie chart | `help:///skills/mermaid-diagrams/pie.md` |
| Prioritization (2D) | Quadrant chart | `help:///skills/mermaid-diagrams/quadrant.md` |
| Trend/metric data | XY chart | `help:///skills/mermaid-diagrams/xy-chart.md` |
| Service topology (20+) | Architecture | `help:///skills/mermaid-diagrams/architecture.md` |
| Hierarchical proportions | Treemap | `help:///skills/mermaid-diagrams/treemap.md` |
| Workflow stages | Kanban | `help:///skills/mermaid-diagrams/kanban.md` |

**Decision tree in your head**:
1. Linear sequence? Use a list.
2. Shows relationships? Proceed.
3. What type of relationship? (Process, interaction, structure, time, concept)
4. Consult reference file for that type.

---

## Accessibility Requirements

**Non-negotiable**:
- Black text on light backgrounds (`color:#000`)
- 3:1 contrast minimum (WCAG AA)
- Never rely on color alone - use shapes, labels, borders
- Always explain colors in comments
- Test in both light and dark mode

**Suggested semantic palette**:
```css
--success: fill:#90EE90,stroke:#2E7D32,color:#000
--error: fill:#FFB6C1,stroke:#C62828,color:#000
--warning: fill:#FFE082,stroke:#F57C00,color:#000
--info: fill:#81D4FA,stroke:#0277BD,color:#000
```

---

## Quick Validation

Before committing:
- [ ] No linear sequences (use list instead)
- [ ] Labels with spaces are quoted
- [ ] Colors explained in comments
- [ ] Not relying on color alone
- [ ] <12 nodes (split if larger)
- [ ] Right diagram type for content

---

*Diagrams are expensive. Make them earn their place.*
