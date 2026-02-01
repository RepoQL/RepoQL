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

## Decision Support

The primary value of reference docs is helping readers choose correctly. Decision tables beat prose.

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

Decision tables are more useful than "when to use / when not to use" prose because:
- Tables are scannable
- Rows map needs to solutions directly
- No boilerplate padding

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
- **Tables over prose** — faster to scan
- **Code blocks stand out** — easy to spot examples
- **Alphabetical or logical grouping** — predictable order

## Common Mistakes

Call out mistakes explicitly with problem and fix:

```markdown
| Mistake | Problem | Fix |
|---------|---------|-----|
| `src/**test` | `**` not a complete segment | `src/**/test*` |
| Using `%` wildcard | That's SQL LIKE, not glob | Use `**` and `*` |
```

This is more useful than abstract "when not to use" guidance because it shows *specific* errors readers actually make.

## Structure

```markdown
# [Topic]

[One sentence: what this covers]

## Quick Reference

| Need | Solution |
|------|----------|
| ... | ... |

---

## [Category A]

[Brief explanation]

```
[Examples]
```

---

## [Category B]
...

---

## Common Mistakes

| Mistake | Problem | Fix |
|---------|---------|-----|
| ... | ... | ... |
```

## Worth Asking

Before writing:
- Is this for lookup or understanding? (If understanding, consider gestalt)
- How many entries? (If 50+, consider queryable format)
- Who's the primary audience? (Affects format choice)
- What decision does this support?

After drafting:
- Is every entry the same structure?
- Are examples copy-paste ready?
- Can someone find what they need in under 30 seconds?
- Is there a decision table at the top?

## Checklist

- [ ] Format chosen deliberately (markdown, queryable, or hybrid)
- [ ] If hybrid: flat facts in CSV, relational context in markdown
- [ ] Decision table near the top for common needs
- [ ] Examples are copy-paste ready
- [ ] Common mistakes table at the end
- [ ] Scannable in under 30 seconds
- [ ] Clear boundary with gestalt (lookup vs understanding)

*Find the answer. Don't read the book.*
