# Writing Gestalt Documents

Gestalt captures the essential essence of a thing. One page that re-hydrates understanding and enables good instincts in later work.

### Capsule: GestaltTest

**Invariant**
After reading, do you UNDERSTAND the space? Can you reason about it, not just navigate it?

**Example**
Read 50 service gestalts → understand the architecture. Know what each does, how they relate, what owns what.
//BOUNDARY: Not orientation (reference). Not how-to (process). Comprehension.

**Depth**
- Can explain it to someone else
- Can predict behavior in new situations
- Know where new things would fit
- Have the "aha!" that makes everything click

---

## What Gestalt Does

**Re-hydrates knowledge.** You knew this once, or you're learning it fresh. The gestalt gives you the essential essence in minutes, not hours.

**Enables good instincts.** After reading, you can make reasonable decisions about where things belong, what's affected by changes, how to approach problems.

**Scales across systems.** 1 page per service × 50 services = understand the architecture. Know what the system does, how, and what component is responsible for each part.

**Closely related to concepts.** Capsules capture crystallized wisdom. Gestalts capture crystallized understanding of a specific thing.

---

## The Essential Essence

### Capsule: EssentialEssence

**Invariant**
Gestalt captures what makes this thing THIS thing, not everything about it.

**Example**
Identity service: "JWT authority for the platform. Event-sourced. Identity merging is irreversible. Everything depends on it."
//BOUNDARY: If you had to compress understanding to one paragraph, what survives?

**Depth**
- What it IS, not what it does in detail
- The constraints that shape everything
- The relationships that matter
- The gotchas that burn people

Not comprehensive. Sufficient for reasoning.

---

## Structure

A gestalt answers these questions, roughly in this order:

1. **What is this?** — One paragraph. The essential essence.
2. **What does it do?** — Core capabilities, not feature list.
3. **How does it fit?** — Dependencies, dependents, position in the system.
4. **What concepts matter?** — 2-4 key ideas that unlock understanding.
5. **What are the constraints?** — What shapes decisions about this thing.
6. **Where to go deeper?** — Pointers to reference, design docs, code.

```markdown
# [Thing] Gestalt

[One paragraph: what this is and why it exists]

## What It Does

[Core capabilities — what problems it solves, not how]

## How It Fits

[Dependencies, dependents, position in larger system]
[Diagram if relationships are complex]

## Key Concepts

### [Concept 1]
[2-3 sentences that unlock understanding]

### [Concept 2]
[2-3 sentences]

## Constraints

[What shapes decisions — technical, business, historical]

## Deeper

- [Design doc](link) — architectural decisions
- [Reference](link) — lookup details
- [Code](link) — implementation
```

---

## What Gestalt Is NOT

| Gestalt | Not Gestalt |
|---------|-------------|
| Essential essence | Comprehensive coverage |
| Understanding | Navigation |
| 2-4 key concepts | Feature list |
| Why it's this way | How to use it |
| One page | Long-form documentation |
| Re-hydrates knowledge | Teaches from scratch |

**Reference** helps you find things when you know what you need.
**Gestalt** helps you understand before you know what questions to ask.

---

## Worth Asking

Before writing:
- What makes this thing THIS thing?
- If I had one paragraph, what survives?
- What 2-4 concepts unlock understanding?
- What constraints shape everything?
- What burns people who don't know?

After drafting:
- Could someone explain this to a colleague after reading?
- Could they predict behavior in new situations?
- Is it one page? If not, what can be cut or linked?
- Does it enable good instincts, or just provide facts?

---

## Progressive Disclosure

Gestalt is the entry point. It provides understanding, then points elsewhere:

```
Gestalt (understand)
    → Reference (lookup details)
    → Design (architectural decisions)
    → Code (implementation)
```

Don't duplicate what lives elsewhere. Point to it.

---

## Checklist

- [ ] Essential essence in first paragraph
- [ ] One page or less
- [ ] 2-4 key concepts, not feature list
- [ ] Relationships and position clear
- [ ] Constraints that shape decisions
- [ ] Pointers to depth, not duplication
- [ ] Reader could explain it to someone else
- [ ] Reader could predict behavior in new situations

---

## Exemplar

See `exemplar.md` — Identity service gestalt.

*The essential essence, not everything. Understanding, not just facts.*
