# Writing Reference Documents

Reference documents store knowledge for lookup. Not prose, not explanation — structured entries that answer "how do I do X?" without making you read everything else.

## When Reference, When Gestalt

| Need | Document Type |
|------|---------------|
| "What is X and how does it fit?" | Gestalt |
| "How do I use X?" | Reference |
| "Should I use X or Y?" | Reference (decision support) |
| "Why does X work this way?" | Gestalt |

Reference assumes you already know you need the thing. It helps you use it correctly. If you need to understand the thing first, that's gestalt.

## Markdown vs Queryable Format

Not everything belongs in markdown. Consider the format that best serves how the content will be used.

**Markdown when:**
- Human-primary audience
- Entries benefit from narrative context
- Decision trees need visual layout
- Code examples need syntax highlighting
- Fewer than ~50 entries
- Content rarely changes

**Queryable format (JSON, YAML, CSV, SQLite) when:**
- Agent-primary audience
- Many similar entries (50+)
- Need to filter or search programmatically
- Data feeds other tools or generates views
- Structure is uniform across all entries
- Content updates frequently

### Capsule: DataGuidanceSplit

**Invariant**
Data goes in CSV. Guidance goes in markdown.

**Example**
Service catalog: `services.csv` (facts, notes, GUIDs), `service-calls.csv` (relationships), `service-catalog.md` (how to query, maintenance).
//BOUNDARY: If it fits a row, CSV. If it explains how to use the rows, markdown.

**Depth**
- All facts including descriptions and notes → CSV
- Relationships as separate CSVs (calls, events-publish, events-consume)
- Markdown provides: common queries, pre-computed summaries, maintenance procedures
- Multiple CSVs better than one wide CSV with nulls

### Capsule: EnumerateOrDiscover

**Invariant**
Enumerate finite sets; teach discovery for infinite sets.

**Example**
50 services → list them all. 1000+ metrics → teach naming patterns and discovery queries.
//BOUNDARY: If enumeration would go stale or be incomplete, teach discovery instead.

**Depth**
- Enumerate when: finite, stable, countable (services, error codes, diagram types)
- Teach discovery when: too large, dynamic, unbounded (metrics, API endpoints, config)
- Discovery patterns: naming conventions, query templates, exploration techniques
- Tables only for common/critical items when teaching discovery

## Entry Structure

Every entry in a reference document should follow the same structure. Consistency is the point — readers learn the shape once, then scan.

### Capsule: WhenNotToUse

**Invariant**
Every reference entry needs "When NOT to use." If everything is always appropriate, you don't need a reference.

**Example**
`analyze_golden_metrics`: When NOT to use → "You already know the specific problem" or "You need raw data, not AI analysis."
//BOUNDARY: Prevents misuse. Enables decision-making. Not optional.

**Depth**
- Minimum entry: When to use, When NOT to use, Example
- If you can't articulate when NOT to use, the entry lacks precision
- "When NOT to use" often more valuable than "When to use"

Minimum viable entry:
```markdown
### [Entry Name]

**When to use:** [Conditions where this is the right choice]

**When NOT to use:** [Conditions where this is wrong]

**Example:**
[Copy-paste ready code or configuration]
```

## Decision Support

Reference documents often help choose between options. Structure for comparison:

**Decision tables:**
```markdown
| Need | Use |
|------|-----|
| Simple key-value | Dictionary |
| Ordered collection | List |
| Unique items | HashSet |
```

**Decision trees:**
```markdown
Is ordering important?
├─ Yes → Is random access needed?
│        ├─ Yes → List
│        └─ No → Queue or Stack
└─ No → Are items unique?
         ├─ Yes → HashSet
         └─ No → List (or reconsider)
```

**Comparison tables:**
```markdown
| Feature | Option A | Option B |
|---------|----------|----------|
| Performance | O(1) | O(n) |
| Memory | Higher | Lower |
| Thread-safe | No | Yes |
```

## Copy-Paste Ready

Examples should work when copied directly. This means:
- Complete, not fragments
- Realistic values, not placeholders like `<your-value-here>`
- Import statements included if relevant
- Comments explaining non-obvious parts

```csharp
// Good: copy-paste ready
var client = new HttpClient();
client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
var response = await client.GetAsync("https://api.example.com/users");

// Bad: requires modification
var client = new HttpClient();
// Add your headers here
var response = await client.GetAsync("<API_URL>");
```

## Scannability

Readers don't read reference docs — they scan for what they need. Support this:

- **Consistent headers** — same depth, same naming pattern
- **Bold key terms** — "When to use:", "Returns:", "Throws:"
- **Tables over prose** — faster to scan
- **Code blocks stand out** — easy to spot examples
- **Alphabetical or logical grouping** — predictable order

## Worth Asking

Before writing:
- Is this for lookup or understanding? (If understanding, consider gestalt)
- How many entries? (If 50+, consider queryable format)
- Who's the primary audience? (Affects format choice)
- What decision does this support?

After drafting:
- Is every entry the same structure?
- Does every entry have "When NOT to use"?
- Are examples copy-paste ready?
- Can someone find what they need in under 30 seconds?

## Structure

```markdown
# [Topic] Reference

[One sentence: what this reference covers and when to use it]

## When to Use This Reference

[Conditions where this document helps]

## When NOT to Use This Reference

[What this doesn't cover, where to look instead]

---

## [Category A]

### [Entry 1]

**When to use:** [Conditions]

**When NOT to use:** [Conditions]

**Example:**
[Code or configuration]

### [Entry 2]
...

---

## [Category B]
...

---

## Quick Reference

[Summary table or decision tree for common choices]
```

## What to Avoid

| Pattern | Problem |
|---------|---------|
| Prose explanations | That's gestalt territory |
| Inconsistent entry structure | Breaks scanning |
| Missing "When NOT to use" | Enables misuse |
| Placeholder examples | Not copy-paste ready |
| Everything in markdown | Queryable might serve better |
| No decision support | Just a list, not a reference |

## Checklist

- [ ] Format chosen deliberately (markdown, queryable, or hybrid)
- [ ] If hybrid: flat facts in CSV, relational context in markdown
- [ ] Every entry follows same structure
- [ ] "When NOT to use" present for every entry
- [ ] Examples are copy-paste ready
- [ ] Decision support included (tables, trees)
- [ ] Scannable in under 30 seconds
- [ ] Clear boundary with gestalt (lookup vs understanding)

## Exemplar

See `exemplar.md` — service catalog demonstrating:
- Consistent entry structure (every service same fields)
- Quick lookup table for common needs
- Blast radius reference for decision support
- Entry template for maintenance

Also see in the wild:
- `docs/guidance/newrelic/service-catalog.md` — full service catalog (enumerate pattern)
- `docs/guidance/newrelic/metrics-catalog.md` — metrics discovery (teach discovery pattern)

*Find the answer. Don't read the book.*
