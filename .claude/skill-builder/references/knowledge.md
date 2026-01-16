# Writing Knowledge-Heavy Skills

## Where You Are

You scored high on Knowledge. You're capturing understanding Claude lacks—mental models, concepts, and the tools to find details.

## Core Objective

Help Claude think correctly about a domain, not just look up values.

## The Key Distinction

Knowledge: facts and models Claude lacks.
Explanation: facts Claude has but needs prompted.

If Claude could figure it out by reasoning, it belongs elsewhere.

## Worth Exploring

- What mental model are you conveying?
- What does Claude get wrong without this?
- Are you providing tools to find details, or exhaustively listing them?
- Could someone use this to think correctly about the domain?

On examples:
- Do your examples tell the right story?
- What scope do your examples imply?
- Will this example echo helpfully or harmfully?

On structure:
- Is this a concept (capsule) or a reference (table/schema)?
- Can pieces be retrieved independently?
- Where do the details live?

## Structure That Works

**Concepts** → Capsule format (Invariant → Example → Depth)

**Reference data** → Tables, schemas, structured lookups

**Details that live elsewhere** → Point to the source of truth; don't duplicate what will rot

**Details that don't live anywhere** → Include them; your skill IS the source of truth

**Examples** → Few and carefully chosen; they have semantic weight

## Reference File Granularity

Split reference files as much as possible without losing decision context.

**Split when**: Topics are independent; reading one without the other still leads to correct decisions.

**Consolidate when**: Half the information would lead to wrong decisions; the topics are coupled.

**Move up when**: Information is cross-cutting; multiple references need the same context to avoid duplication.

The goal: don't pollute context with unrelated information, but don't force multiple reads for a single decision.

## Required Practices

- Capture mental models, not just facts
- Use capsules for concepts
- Point to sources of truth; don't duplicate what will rot
- If your skill IS the source, include what Claude needs
- Choose examples that tell the right story and imply correct scope
- Token-efficient naming (CamelCase)
- Progressive disclosure: gestalt in SKILL.md, depth in reference files

## Success Looks Like

- Claude thinks correctly about the domain
- Right concept surfaces for right query
- Examples echo helpfully through subsequent work
- Details are findable when needed
- No wasted tokens on what Claude already knows

## The Transformation

You will write this:
```
GET /users
GET /users/:id
POST /users
PUT /users/:id
GET /payments
GET /payments/:id
POST /payments
...47 more endpoints...
```

Write this instead:
```
Resources follow RESTful path conventions:
- GET /{resource} - list collection
- GET /{resource}/:id - get individual
- POST /{resource} - create
- PUT /{resource}/:id - update

Exception: bulk operations use POST /{resource}/batch

See api-reference.md for resource-specific exceptions.
```

One lists instances. One captures the pattern that generates them.

## Scripts as Living Knowledge

Scripts can fetch truth instead of documenting it. This avoids rot.

Instead of listing endpoints that will change:
```
scripts/list-api-endpoints.sh
→ Output: current endpoints from OpenAPI spec
```

The skill documents the pattern; the script provides current facts.

**When to use scripts for knowledge:**
- Data that changes (API endpoints, config values, versions)
- Information with a queryable source of truth
- Generated content (reports, summaries)

Only the script's output consumes tokens—the script itself is free.

## Exemplars

**For concepts:** `docs:///guidance/writing-and-documentation/writing-capsules.md` - capsule format demonstrating capsule format.

**For reference material:** `docs:///guidance/writing-and-documentation/mermaid-diagram-guide.md` - mental model first, decision tests, structured entries, examples that tell the right story, details that ARE the source of truth.

## Template

See `knowledge-template.md` - three structural patterns for concept-heavy, reference-heavy, and schema-heavy skills.

## Final Thought

Give them the map and the compass, not the territory.
