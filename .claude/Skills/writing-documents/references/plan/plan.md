# Writing Plan Documents

Plans make designs executable. They take a chunk of architecture and make it buildable — specific enough to know when done, open enough to trust the agent.

## Capsule: TwoAudiences

**Invariant**
Plans are reviewed by humans and implemented by agents; structure must serve both.

**Example**
Humans ask: "Is this the right scope? Does it align?" — front-load context (Enables, North Star).
Agents ask: "What exactly must I build?" — EARS syntax, actionable references, error policy.
//BOUNDARY: Order sections for human review flow; ensure agent-critical information is unambiguous.

**Depth**
- Humans need quick assessment (under 5 minutes); context before details
- Agents need precision; no stopping to ask questions
- Same content serves both when well-structured

## Two Audiences

Plans are reviewed by humans and implemented by agents. Structure for both.

**Humans reviewing** need quick orientation. They're asking: Is this the right scope? Does it align with the design? Are we ready to build? Front-load context (Enables, North Star) so they can assess without reading every line. Make constraints traceable to design decisions so they can verify alignment.

**Agents implementing** need unambiguous requirements. They're asking: What exactly must I build? How do I know when I'm done? What can't I do? Make done criteria precise with EARS syntax. Make references actionable with real links and package names. State error policy so they don't have to stop and ask.

Order sections for human review flow (context first, details second) while ensuring agent-critical information is complete and unambiguous.

## Sections

### Scope

What this plan covers and what it doesn't. Explicit boundaries prevent scope creep and clarify ownership.

```markdown
**Covers:**
- Component A
- Component B

**Does not cover:**
- Component C (Plan: Other Plan)
```

### Enables

Orient the reader in the design. What becomes possible once this exists? What downstream work depends on it?

This is primarily for human reviewers — it answers "why does this matter?" and helps them assess priority. For agents, it clarifies what other work is blocked on this.

### Prerequisites

What must exist before starting. Be specific — not "auth must work" but "Identity service JWT validation endpoint operational."

Prerequisites reveal dependencies on other plans. Call them out: "Requires EventBus from Plan: Messaging Infrastructure."

### North Star

What great looks like for this piece, inherited and narrowed from the design. Must be specific enough to evaluate — not "fast enough" but "1000 files in under 2 seconds."

### Done Criteria

How you know it's complete. Use EARS (Easy Approach to Requirements Syntax) for testable statements:

| Pattern | Template |
|---------|----------|
| Ubiquitous | The [system] shall [response] |
| Event-driven | When [trigger], the [system] shall [response] |
| State-driven | While [state], the [system] shall [response] |
| Optional | Where [feature], the [system] shall [response] |
| Unwanted | If [condition], then the [system] shall [response] |

If you can't write an EARS statement for it, you don't know what done means.

**Group related criteria** for scannability. Humans need to review the shape; agents need the precision:

```markdown
### FileScanner
- The FileScanner shall identify files matching supported extensions
  - When no supported files found, return empty result
- The FileScanner shall detect changes by comparing hashes
  - New: present on disk, absent from store
  - Modified: hash differs
  - Deleted: in store, not on disk
```

### Constraints

What you can't do and why. Three types:

- **Ownership boundaries** — "Notifications are Plan: Notifications scope"
- **Technical restrictions** — "Cannot use Redis; design specifies SQLite"
- **Design goal alignment** — "Must not require configuration; violates north star"

Constraints must trace to design decisions. If a reviewer can't find the rationale in the design, the constraint needs justification or removal.

### References

Pointers to relevant guidance — don't embed it, link it:

- Design document this plan implements
- Testing patterns and conventions
- Related plans (dependencies, handoffs)
- External dependencies with actionable links
- Error handling policy

**Make references actionable.** Not "ParquetSharp documentation" but "[ParquetSharp](https://github.com/G-Research/ParquetSharp) — `G-Research.ParquetSharp` NuGet package."

**Include error policy.** Either state it directly ("errors should not prevent operation from completing; log and continue") or link to where it's defined.

## Worth Asking

Before writing:
- What chunk of the design does this cover?
- What becomes possible once this exists?
- What must exist before this can start?
- Can I write EARS statements for every done criterion?
- Would a reviewer find every constraint in the design?

After drafting:
- Can a human assess this in under 5 minutes?
- Could an agent implement this without stopping to ask questions?
- Are references actionable, not decorative?

## Structure

```markdown
# Plan: [Component/Phase Name]

Implements: [link to design section]

## Scope

**Covers:**
- [What's included]

**Does not cover:**
- [What's excluded and where it lives]

## Enables

[What becomes possible, what depends on this]

## Prerequisites

- [Specific dependency with location]

## North Star

[Specific, measurable definition of great]

## Done Criteria

### [Group A]
- The [system] shall [response]
  - [Sub-criteria as needed]

### [Group B]
- When [trigger], the [system] shall [response]

## Constraints

- **[Type]**: [Constraint] — [traceable rationale]

## References

- [Design doc](link) — what it covers
- [Package](link) — `package.name`

## Error Policy

[How to handle failures, or link to general policy]
```

## What to Avoid

| Pattern | Problem |
|---------|---------|
| Vague done criteria | "Works correctly" isn't testable |
| Ungrouped EARS | Wall of text humans can't scan |
| Implementation details | "Use character count" is the agent's call |
| Decorative references | "See docs" without links |
| Missing error policy | Agent stops to ask |
| Orphaned plans | No link to design loses context |
| Untraceable constraints | Reviewer can't verify alignment |

## Lifecycle

Plans evolve during execution. When work reveals unknowns, update scope, add discovered constraints, refine ambiguous EARS statements.

Once work is complete, delete the plan. The design and the code are the durable artifacts — the plan served its purpose.

## Checklist

- [ ] Scope clearly bounded with explicit exclusions
- [ ] Enables articulates value and downstream dependencies
- [ ] Prerequisites specific and locatable
- [ ] North star specific and measurable
- [ ] Done criteria grouped and in EARS syntax
- [ ] Constraints traceable to design decisions
- [ ] References actionable with real links
- [ ] Error policy stated or linked
- [ ] Human can assess in under 5 minutes
- [ ] Agent can implement without stopping to ask

## Exemplar

See `exemplar.md` — FileScanner component from semantic search design.
