---
description: "Quickstart for RepoQL - URIs, budget, finding help, schema and documentation maps"
tags: ["quickstart", "orientation", "help", "schema", "documentation"]
audience: ["LLMs"]
categories: ["Guide[100%]"]
---

# RepoQL Quickstart

> You've read the server description. This teaches the patterns.

---

## Orient First

See the shape before reading anything:

```
read("file:///** => tree: folders", 500)
```

This costs ~100 tokens and shows every directory. Start here, always.

---

## URIs: The Foundation

Everything in RepoQL is addressed by URI. Master this syntax.

**Schemes:**
| Scheme | Example | What it addresses |
|--------|---------|-------------------|
| `file:///` | `file:///src/Auth.cs` | Repository files |
| `help:///` | `help:///quickstart.md` | Embedded documentation |
| `github://` | `github://owner/repo@ref` | Imported external repos |

**Fragments** pinpoint within files:
```
file:///src/Auth.cs#line=42           -- single line
file:///src/Auth.cs#line=42,100       -- line range (inclusive, 1-based)
file:///src/Auth.cs#symbol=AuthService      -- exact symbol
file:///src/Auth.cs#symbol=AuthService.*    -- direct children
file:///src/Auth.cs#symbol=AuthService.**   -- all descendants
```

**Globs** select many:
```
src/**/*.cs              -- recursive, all .cs files
src/*.cs                 -- non-recursive, one level
src/**;lib/**            -- combine with ;
src/**;!**/tests/**      -- exclude with !
**/*.cs;!**/*.g.cs       -- multiple exclusions (AND logic)
```

**Combine everything:**
```
file:///src/**/*.cs;!**/tests/**#symbol=*Service
```

---

## Finding Help

The documentation is indexed. Search it like code.

**See what exists:**
```
read("help://** => tree: folders", 500)
```

**Find a topic:**
```
explore(intent="Locate", scope="help://**", keywords="modifiers", tokenBudget=1500)
```

**Read what you found:**
```
read("help:///repoql/tools/read/modifiers.md", 2000)
```

This is the explore → read pattern. It works for code and docs.

---

## Feature Map

What exists, where to learn more.

### Read Modifiers

Append `=> modifier` to any read:

| Modifier | Purpose | Detail levels |
|----------|---------|---------------|
| `tree` | Directory structure | `folders`, `files`, `headlines` |
| `history` | Git commits | `: keyword` filters by message/author |
| `blame` | Line-by-line attribution | who changed each line |
| `lint` | Diagnostics | `: errors`, `: warnings` |
| `question` | LLM synthesis | `: your question here` |
| `headline` | One-line summaries | — |
| `structure` | Signatures only | — |

See `help:///repoql/tools/read/read-command.md`

### Query Views

Start with views, not raw tables:

| View | Purpose | Key columns |
|------|---------|-------------|
| `Files` | Document inventory | uri, lang, lines, error_count, headline |
| `Types` | Classes, interfaces | name, type_kind, namespace, extends |
| `Functions` | Methods, constructors | name, signature, declaring_type |
| `Annotations` | Lint, metrics | severity, message, resolved_target_uri |

See `help:///repoql/tools/query/schema.md`

### Query Functions

| Function | Purpose |
|----------|---------|
| `search(q, k)` | Semantic + lexical search |
| `snippet(uri, context)` | Code preview around location |
| `git_blame(uri)` | Line-by-line attribution |
| `git_hotspots` | Churn analysis (view) |
| `mcp_tools()` | List available MCP servers |
| `read_csv(uri)` | Parse CSV into rows |
| `xlsx(uri)` | Parse Excel into rows |

See `help:///repoql/tools/query/sql-reference.md`

---

## Schema Map

Five frozen tables, extend via views/macros/UDFs:

| Table | Purpose | Key columns |
|-------|---------|-------------|
| `artifact` | Content + X-ray | headline, summary, structure, text_content |
| `node` | Graph vertices | kind, uri, properties, artifact_id, span_id |
| `edge` | Relationships | type, is_composition, source/destination_node_id |
| `span` | Locations | start_line, end_line (1-based inclusive) |
| `annotation` | Out-of-band facts | kind, severity, message, target_* |

**Key insight:** Documents have `artifact_id`. Child symbols have `span_id`. Never both.

See `help:///repoql/tools/query/schema.md`

---

## Documentation Map

| Path | Contents |
|------|----------|
| `help:///quickstart.md` | You are here |
| `help:///repoql/tools/explore/` | Explore tool, intents |
| `help:///repoql/tools/read/` | Read tool, modifiers |
| `help:///repoql/tools/query/` | Query tool, schema, SQL reference |
| `help:///repoql/tools/query/functions/` | Function reference |
| `help:///guidance/` | Meta: writing documentation |

---

## Quick Patterns

**Orient:**
```
read("file:///** => tree: folders", 500)
```

**Find:**
```
explore(intent="Locate", keywords="authentication", tokenBudget=1500)
```

**Understand:**
```
explore(intent="Explain", keywords="How does caching work?", tokenBudget=2500)
```

**Read specific:**
```
read("file:///src/Auth.cs#symbol=ValidateToken", 2000)
```

**Aggregate:**
```sql
SELECT lang, COUNT(*), SUM(lines) FROM Files GROUP BY lang;
```

**Who changed this:**
```
read("file:///src/Auth.cs => blame", 2000)
```

**What's broken:**
```sql
SELECT resolved_target_uri, message FROM Annotations WHERE severity = 'error';
```
