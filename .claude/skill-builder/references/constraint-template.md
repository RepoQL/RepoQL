# Constraint Skill Template

This template is a compass, not a form. Constraints should be short. Don't let the template make them long.

---

## Capsule: ConstraintRule

**Invariant**
A constraint rule states what must or must not happen, with scope and enforcement.

**Example**
```
- Never log credentials

Scope: all services
Enforcement: CI log scanner
```
//BOUNDARY: If you're explaining why, you're writing knowledge, not constraint.

**Depth**
- Rule first, then scope, then enforcement
- Exceptions only if they exist and are explicit
- No justification needed

---

## Structural Pattern

```yaml
---
name: skillName
description: [Domain] constraints and boundaries. Use when [context].
zones: { K: 25, P: 5, C: 60, W: 10 }
---
```

```markdown
# [Constraint Domain]

[One line: what these constraints protect]

## Rules

- [Must/Must not statement]
- [Must/Must not statement]
- [Must/Must not statement]

## Scope

[Where these apply]
[Where these do NOT apply]

## Enforcement

[How compliance is verified—tooling, review, audit]

## Exceptions

[If any exist, state them explicitly]
[Who can approve exceptions, under what conditions]
```

---

## Deciding What to Include

| Element | Include when... |
|---------|-----------------|
| Scope | Not obvious from context |
| Enforcement | Tooling or process exists |
| Exceptions | Legitimate exceptions exist |
| Context/definitions | Terms need clarification (this is knowledge—keep brief) |

---

## Self-Check

- Is deviation truly unacceptable?
- Are rules specific enough to follow?
- Am I explaining instead of stating?
- Is scope explicit?
- Are exceptions documented if they exist?

---

*If you need a paragraph to state the rule, it might be guidance.*
