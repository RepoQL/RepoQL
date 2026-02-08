---
description: Vision for turning arbitrary text data into queryable tables — any format, any source, zero ceremony
tags: [parse, structured, formats, query, MCP, data, tables]
audience: { human: 50, agent: 50 }
purpose: { north-star: 100 }
---

# Parsing: What Great Looks Like

> Any data that arrives as text becomes a table. The agent never thinks about format.

An agent calls an MCP tool and gets back a wall of CSV. It calls another and gets JSON. A third returns a markdown table. A fourth returns YAML. The agent doesn't notice. It writes SQL against the results — joins them, filters them, aggregates them — as if they were always tables. The format was the tool's choice. The structure is the agent's right. No parsing code, no format flags, no "let me convert this first." Data arrives, data is queryable. The gap between receiving information and reasoning over it disappears.

---

## Format Transparency

- An agent should be able to query tool results without knowing what format they arrived in
- An agent should be able to get the same table whether the source returned JSON, CSV, TSV, YAML, JSONL, or a markdown table
- An agent should be able to trust that format detection is correct without hinting — the data speaks for itself
- An agent should be able to override detection when the data doesn't speak clearly enough

```sql
-- The agent doesn't know or care what format this returned
SELECT name, status FROM server_list_instances() WHERE status = 'running'
```

---

## Schema Discovery

- An agent should be able to query columns by name without declaring a schema
- An agent should be able to trust that types are inferred correctly — numbers are numbers, dates are dates, booleans are booleans
- An agent should be able to query nested structures without flattening them first
- An agent should be able to discover what columns exist by querying, not by reading documentation

---

## Fidelity

- An agent should be able to trust that parsing preserves meaning, not serialization artifacts
- An agent should be able to get `null` for missing values, not empty strings or the word "null"
- An agent should be able to trust that a number that looks like a number behaves like a number — `price > 100` works
- An agent should be able to trust that arrays stay arrays and objects stay objects — structure survives the round trip

---

## Unwrapping

- An agent should be able to query the data inside a response wrapper without knowing the wrapper's shape — `{"status": "ok", "results": [...]}` should yield the array, not a single-row table with a `results` column
- An agent should be able to get a table from deeply nested data — `response.data.items[...]` should still become rows
- An agent should be able to trust that the parse finds the most table-like structure in the response — the largest array of objects, not the envelope
- An agent should be able to access envelope metadata when it needs to — pagination cursors, total counts, status codes — without it polluting every row
- An agent should be able to override unwrapping when the wrapper itself is the data it wants

```sql
-- Tool returns: {"total": 312, "page": 1, "items": [{"name": "foo"}, {"name": "bar"}]}
-- Agent gets a table of items, not a table with one row containing a JSON array:
SELECT name FROM tool_result()

-- But can still reach the envelope when needed:
SELECT * FROM tool_result(unwrap := false)
```

---

## Messy Data

- An agent should be able to query data that arrives wrapped in prose — "Here are the results:\n```json\n[...]```"
- An agent should be able to query data with inconsistent whitespace, trailing commas, or missing quotes
- An agent should be able to get useful results from data that's mostly well-formed with a few bad rows
- An agent should be able to query data that arrives as a single object just as easily as data that arrives as an array

```
-- Tool returned: "Found 3 servers:\n\n| Name | Region | Status |\n|------|--------|--------|\n| web-1 | us-east | running |\n..."
-- Agent just queries it:
SELECT name FROM tool_result WHERE region = 'us-east'
```

---

## Composition

- An agent should be able to join parsed results with the code graph in a single query
- An agent should be able to chain tool calls where one tool's output feeds another's input
- An agent should be able to use parsed results in CTEs, subqueries, and window functions like any other table
- An agent should be able to combine results from multiple tools with different source formats in one query

```sql
-- Join MCP tool output with the code graph
SELECT f.uri, f.headline, d.status
FROM Files f
JOIN deploy_service_status() d ON d.service_name = f.headline
WHERE d.status = 'failing'
```

---

## Source Independence

- An agent should be able to parse data from any source — MCP tools, HTTP responses, file contents, inline text — with the same behavior
- An agent should be able to use `parse` as a standalone function, not only as part of the MCP macro chain
- An agent should be able to parse a column of text values into structured results within a query

```sql
-- Parse inline data
SELECT * FROM parse('[{"a": 1}, {"a": 2}]')

-- Parse a column
SELECT p.* FROM raw_responses r, parse(r.body) p
```

---

## Error Honesty

- An agent should be able to distinguish "no data" from "couldn't parse"
- An agent should be able to see what went wrong when parsing fails — the raw input, the detected format, and why it didn't work
- An agent should be able to get partial results when some rows parse and others don't
- An agent should never get silent wrong answers — a confident wrong table is worse than a loud failure

---

## Token Efficiency

- An agent should be able to trust that parsing doesn't inflate data — 50 rows of CSV shouldn't become 50 verbose JSON objects in the response
- An agent should be able to query parsed results with SQL and only receive the rows and columns it asked for
- An agent should be able to parse large results without the intermediate representation consuming its context

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Query tool results without knowing their format | Format is the source's problem, not the agent's |
| Get correct types without declaring a schema | Numbers, dates, booleans just work |
| Handle data wrapped in prose or code fences | Real tools return messy output |
| Join parsed results with the code graph | One query surface for everything |
| Use parse standalone, not just in MCP macros | Any text becomes a table, anywhere |
| Get loud failures, never silent wrong answers | Trust in every result |
| Parse without inflating token cost | Efficiency is a feature, not a side effect |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Require format hints | An agent should get correct results from data alone |
| Silently return empty for unparseable input | An agent should see why parsing failed |
| Only work inside MCP macro chain | An agent should parse any text in any context |
| Stringify everything | An agent should get real types — numbers, booleans, dates |
| Require clean input | An agent should handle prose-wrapped, inconsistent, real-world data |
| Explode compact formats into verbose ones | An agent should trust that parsing doesn't waste tokens |

---

*An agent should be able to turn any text into a table and query it — without knowing or caring what format it arrived in.*
