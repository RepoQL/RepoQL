# Process Skill Template

This template is a compass, not a form. Include what your process needs.

---

## Capsule: ProcessStep

**Invariant**
A process step pairs an action with its verification and recovery path.

**Example**
```
3. Deploy to staging
   → Verify: health checks pass within 5 minutes
   → If failed: check logs, rollback if necessary
```
//BOUNDARY: Steps without verification leave the agent guessing when to proceed.

**Depth**
- Action: what to do
- Verification: how to know it worked
- Recovery: what to do if it didn't
- Not every step needs explicit recovery, but critical steps do

---

## Structural Pattern

```yaml
---
name: skillName
description: [Process name] procedure. Use when [trigger context].
zones: { K: 15, P: 60, C: 20, W: 5 }
---
```

```markdown
# [Process Name]

[One line: what this process achieves and why order matters]

## Prerequisites

- [What must be true before starting]
- [Required access/tools/state]

## Steps

### 1. [Action]

[Brief description if needed]

→ Verify: [How to know it succeeded]
→ If failed: [Recovery action]

### 2. [Action]

...

## Completion

[How to know the process is fully complete]
[Any post-process verification]
```

---

## Deciding What to Include

| Element | Include when... |
|---------|-----------------|
| Prerequisites | Context isn't obvious |
| Verification | Failure at this step causes harm |
| Recovery path | Step can fail and recovery isn't obvious |
| Exact commands | Tool choice matters for safety |
| General direction | Agent can choose appropriate tool |

---

## Self-Check

- Is this actually a process, or a checklist?
- Does each critical step have verification?
- Are failure paths documented?
- Am I prescribing tools where outcomes would suffice?
- Does this process live elsewhere? (Point, don't duplicate)

---

*If steps can be reordered without harm, use a checklist instead.*
