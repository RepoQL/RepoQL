# Extensibility: What Great Looks Like

> An agent should be able to teach RepoQL new tricks — and have them remembered.

An agent analyzes a microservices codebase weekly. The first time, it writes a 12-line SQL macro that joins endpoints across three services, formats the result as a dependency matrix, and pipes it to a markdown file for the team. The second week, it just says `SELECT * FROM service_dependencies` — the macro is already there. It didn't redefine anything. It didn't remember the SQL from last session. The tool remembered. Over months, the repository accumulates a library of domain-specific queries — `compliance_gaps`, `api_surface`, `migration_status` — each defined once by an agent that understood the problem, reused forever by agents that just need the answer. The repository becomes not just indexed, but *understood* — carrying forward the analytical work of every agent that touched it.

---

## Persistence

- An agent should be able to define a SQL macro once and use it in every future session without redefining it
- An agent should be able to create a view that survives reindexing, server restarts, and schema upgrades
- An agent should be able to trust that user-defined extensions are as durable as built-in ones — same availability, same discoverability, same performance
- An agent should be able to delete or replace an extension it previously created

---

## Discovery

- An agent should be able to list all available extensions — built-in and user-defined — through the same query surface
- An agent should be able to search for extensions by name, description, or purpose using the same tools it uses to search code
- An agent should be able to distinguish built-in extensions from user-defined ones when it matters, and ignore the distinction when it doesn't
- An agent should be able to understand what an extension does before using it — every user-defined extension carries a description

```sql
-- The desire path: "what custom queries exist in this repo?"
SELECT * FROM extensions WHERE source = 'user'

-- Or just find one by concept
explore(intent="Locate", keywords="service dependency matrix", uriGlob="extension://**")
```

---

## Definition

- An agent should be able to define an extension using SQL alone — no C#, no compilation, no restart
- An agent should be able to define macros, views, and table-valued functions through the same mechanism
- An agent should be able to attach metadata to an extension — name, description, author, tags — so future agents understand its purpose
- An agent should be able to define an extension that composes other extensions, including built-in ones

```sql
-- One call. Persistent. Discoverable.
CALL save_macro('service_dependencies',
  description := 'Cross-service endpoint dependency matrix',
  sql := '
    SELECT caller.service, callee.service, count(*) as calls
    FROM edges e
    JOIN nodes caller ON e.source_id = caller.id
    JOIN nodes callee ON e.target_id = callee.id
    WHERE e.kind = ''CALLS''
    GROUP BY caller.service, callee.service
  '
);
```

---

## Scope

- An agent should be able to define extensions that live with a specific repository — domain-specific queries that only make sense for that codebase
- An agent should be able to define extensions that follow the user across repositories — personal utilities and formatting preferences
- An agent should be able to tell which scope an extension belongs to
- An agent should be able to use extensions from both scopes simultaneously, with repository-local definitions taking precedence over global ones

---

## Templates

- An agent should be able to define a template that formats query results into a structured document — markdown, HTML, CSV, or any text format
- An agent should be able to pipe a query through a template and write the result directly to a file, without the result passing through agent context
- An agent should be able to compose templates with macros: a stored query feeding a stored template produces a complete report in one call
- An agent should be able to parameterize templates — the same template works for different inputs

```sql
-- Define once
CALL save_template('dependency_report',
  format := 'markdown',
  description := 'Weekly service dependency report',
  body := '# Service Dependencies - {{ date }}
{% for row in rows %}
## {{ row.caller_service }} -> {{ row.callee_service }}
- **Calls**: {{ row.call_count }}
- **Endpoints**: {{ row.endpoints | join: ", " }}
{% endfor %}'
);

-- Use forever
CALL render('dependency_report',
  query := 'SELECT * FROM service_dependencies',
  output := 'reports/dependencies.md',
  params := {'date': '2025-01-15'}
);
```

---

## Composition

- An agent should be able to chain extensions: a macro's output feeds a template's input feeds a file
- An agent should be able to use user-defined macros inside `explore` and `read` the same way it uses built-in ones
- An agent should be able to build higher-level extensions from lower-level ones — a "weekly report" extension that calls three macros and two templates
- An agent should be able to use extensions in `query()` as naturally as any built-in function

```
-- Composition is the desire path
query("SELECT render('api_report', query := 'SELECT * FROM api_surface')")

-- Extensions work inside explore
explore(intent="Inspect", keywords="service health", uriGlob="extension://macros/*")
```

---

## Safety

- An agent should be able to trust that user-defined extensions cannot corrupt the core graph — they read, never write to base tables
- An agent should be able to trust that a broken extension produces a clear error, not silent wrong results
- An agent should be able to trust that extensions are validated at definition time — a macro with a syntax error fails on save, not on first use
- An agent should be able to trust that removing an extension has no side effects beyond removing that extension

---

## What Great Looks Like

| Dimension | Great | Acceptable | Unacceptable |
|-----------|-------|------------|--------------|
| **Persistence** | Define once, available forever. Survives reindex, restart, upgrade | Define once per session, auto-loaded from file on startup | Redefine every session from scratch |
| **Discovery** | Extensions searchable alongside code — same tools, same patterns | Extensions listed via a dedicated command | Extensions only known if you remember the name |
| **Definition** | Pure SQL, one call, immediate availability | SQL file in a known directory, loaded on restart | Requires C# code, compilation, redeployment |
| **Scope** | Repo-local and global, composable, with clear precedence | Repo-local only | One global namespace, no per-repo customization |
| **Templates** | Query-to-file pipeline, zero context tokens for formatted output | Templates exist but agent must receive and relay output | No templates — agent formats everything manually |
| **Composition** | Extensions compose with each other and built-ins seamlessly | Extensions work but can't reference each other | Extensions are isolated from the rest of the SQL surface |
| **Safety** | Validated on save, read-only to core, clear errors | Validated on use, read-only to core | Can modify core tables or produce silent failures |

---

## Anti-Patterns

| Don't | Why | Do Instead |
|-------|-----|------------|
| Require code changes to add user logic | Kills the feedback loop — define, test, refine in one session | Pure SQL definition with immediate availability |
| Store extensions outside the query surface | If you can't find it with `explore`, it doesn't exist | Extensions are queryable, searchable, addressable |
| Make templates a separate system from macros | Two concepts to learn, two APIs, two discovery mechanisms | One extension system with multiple capabilities |
| Require a restart to pick up new extensions | Breaks flow — agent defines, agent uses, same session | Immediate availability after definition |
| Let extensions write to base tables | One bad extension corrupts the graph for everything | Extensions are read-only views over the core |
| Persist extensions in the core database file | Reindex rebuilds the database; extensions vanish | Sidecar storage that survives database rebuilds |

---

## The Accumulation Effect

The most important property of persistent extensions isn't convenience — it's **accumulation**. Each agent session leaves the repository slightly more understood. A macro defined during a debugging session becomes a monitoring query. A template written for one report becomes the standard format. A view created to answer a question becomes the canonical way to ask it.

Without persistence, every agent starts from zero. With it, every agent starts from the combined work of every agent before it. The repository doesn't just have code and an index — it has *institutional memory* encoded in SQL.

---

*An extension defined is knowledge preserved. An extension forgotten is work repeated.*
