# Knowledge Skill Template

This template is a compass, not a form. Match structure to content.

---

## Why Capsules Fit Knowledge

Capsules are the ideal format for encoding concepts:
- Invariant captures the timeless truth (≤30 tokens)
- Example binds it to practice
- Depth clarifies without changing the idea
- Boundary prevents unsafe extrapolation

For concept-heavy knowledge skills, use capsule format throughout. See `docs:///guidance/writing-and-documentation/writing-capsules.md` for full specification.

---

## Capsule: StructuredEntry

**Invariant**
A reference entry uses consistent sections so readers can scan predictably.

**Example**
```
### [Entry Name]

**Use for**: [When this applies]
**Don't use for**: [When to choose something else]
**Example**: [Concrete, working example]
**Best practices**: [Guidance]
**Common mistakes**: [Anti-patterns]
**Critical constraints**: [Hard limits]
```
//BOUNDARY: Sections must be consistent across ALL entries in a reference.

**Depth**
- Pattern from mermaid-diagram-guide.md
- Consistency enables scanning; variation forces reading
- Not all sections required - use what serves the content
- SeeAlso: `docs:///guidance/writing-and-documentation/mermaid-diagram-guide.md`

---

## Capsule: SchemaDefinition

**Invariant**
A schema definition lists fields with types, constraints, and meaning in tabular form.

**Example**
```
## User Schema

| Field | Type | Required | Description |
|-------|------|----------|-------------|
| id | uuid | Y | Primary key |
| email | string | Y | Unique, lowercase |
| role | enum | Y | One of: admin, member, guest |

**Constraints**: email must be valid format; role defaults to "member"
```
//BOUNDARY: If relationships are complex, add a diagram. If business rules dominate, those are constraints, not schema.

**Depth**
- Mental model before field list (what is this entity?)
- Tables for fields; prose for relationships and constraints
- Mark required vs optional; note defaults
- SeeAlso: mermaid ER diagrams for relationships

---

## Choosing Your Shape

Your zone assessment determines the general approach. At a granular level:

| Content Type | Structure | When |
|--------------|-----------|------|
| Concepts | Capsules | Ideas that need invariant + example + boundary |
| Reference catalog | Gestalt + link | Details exist elsewhere; provide mental model and pointer |
| Schema/lookup (small) | Tables | <50 rows, inline reference |
| Schema/lookup (large) | Separate CSV | >50 rows, likely to be joined with other data |

Most knowledge skills blend these. Lead with mental model, then choose structure that fits each section.

---

## Exemplars

**For concepts:** `docs:///guidance/writing-and-documentation/writing-capsules.md` — capsule format demonstrating capsule format.

**For reference material:** `docs:///guidance/writing-and-documentation/mermaid-diagram-guide.md` — mental model first, decision tests, structured entries, examples that tell the right story, details that ARE the source of truth.

---

## Self-Check

- Mental model before syntax?
- Examples tell the right story?
- Pointing to sources of truth (not duplicating what will rot)?
- Pieces retrievable independently?

---

*The exemplars show the patterns. Study them.*
