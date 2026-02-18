---
description: Vision for JSON format support - generic structural indexing plus specialized type detection
tags: [json, format, north-star, classification, addressing]
audience: { human: 50, agent: 50 }
purpose: { north-star: 100 }
---

# JSON: What Great Looks Like

> Same extension, different worlds. An agent should know what a JSON file *is*, not just that it's JSON.

An agent scans 200 `.json` files and immediately sees the landscape from headlines alone: configs with feature flags, OpenAPI specs with endpoint counts, JSON Schema files naming their types, translation bundles grouped by locale, a `package.json` listing its dependencies. It picks the 3 config files relevant to database access, reads their structure, and sees every key path with pointer addresses. It never opens a file. It asks "what feature flags exist across all environments?" and gets one answer from one query. The files spoke different dialects. The agent heard one language.

---

## Classification

- An agent should be able to distinguish a JSON config from a JSON schema from a JSON data file from a JSON API spec from its headline alone
- An agent should be able to trust that well-known filenames (`package.json`, `tsconfig.json`, `appsettings.json`) are classified by what they are, not just by extension
- An agent should be able to see that an unknown `.json` file was classified by its content when the filename is ambiguous
- An agent should be able to see different headlines, different structure, and different query surfaces for different kinds of JSON

---

## Two Tiers

JSON is the most polymorphic format in a repository. Two tiers handle this: a generic base that gives every JSON file real structure, and specific type detectors that add domain intelligence for recognized formats.

### Generic

- An agent should be able to see key paths, nesting, value types, and shape for any JSON file, even one never seen before
- An agent should be able to navigate any JSON file by its key tree without a specific type detector existing for it
- An agent should be able to query key structure across all JSON files through one view, regardless of kind
- An agent should be able to get a meaningful headline for any JSON file that communicates shape and top-level keys

The generic tier is the floor. No JSON file should ever appear as plain text.

### Specific

- An agent should be able to see domain-aware structure for recognized JSON formats: endpoint signatures for API specs, type definitions for schemas, config sections for settings files
- An agent should be able to benefit from a new type detector without changes to the generic tier or other detectors
- An agent should be able to trust that a specific detector produces strictly richer output than the generic tier for the same file
- An agent should not need to know whether a file was handled by the generic tier or a specific detector: the tools and addressing work the same either way

A specific type detector replaces the generic output for files it claims. It produces everything the generic tier would have (key paths, pointer addresses) plus domain-aware structure that answers the format's natural question. An agent that queries the generic key view still sees the file; an agent that queries a domain-specific view sees richer results.

---

## Format Essence

Every kind of JSON has a different natural question:

| Kind | The question |
|------|-------------|
| Generic | "What's in here?" |
| Config | "What knobs exist?" |
| Schema | "What types are defined?" |
| API spec | "What endpoints exist?" |
| Data | "What shape, how much?" |
| i18n | "What strings, what coverage?" |
| Manifest | "What does this declare?" |
| Lock | "What's pinned?" |

- An agent should be able to query each JSON kind in terms natural to that kind
- An agent should be able to ask the same structural question across kinds and get kind-appropriate answers
- An agent should be able to trust that structure reflects the file's essence, not its syntax tree
- An agent should be able to get useful structure from any JSON file even before a specific type detector exists for it

---

## Progressive Disclosure

**Generic** (any JSON file, no special knowledge):

```
headline  →  "rules.json | json | 1.4 KB | 52 ln | ~400 tok | object | rules, version, extends, env, plugins, overrides"
structure →  version: "2.1"                    #/version
             extends (string[], 2 items)       #/extends
             env/ (~50 tok)                    #/env
               browser: true                   #/env/browser
               node: false                     #/env/node
             plugins (string[], 2 items)       #/plugins
             rules/ (~180 tok)                 #/rules
               no-unused-vars: "warn"          #/rules/no-unused-vars
               semi: ["error", "always"]       #/rules/semi
               ...4 more keys
             overrides (object[], 1 item)      #/overrides
content   →  read("file:///rules.json#/rules", 500)
```

**Specific** (recognized formats, domain-aware):

```
headline  →  "appsettings.json | config | 2.1 KB | 85 ln | ~600 tok | Logging, Database, FeatureFlags | cs:DefaultDb, cs:Redis"
structure →  Logging/                          #/Logging
               LogLevel/Default: "Warning"     #/Logging/LogLevel/Default
             Database/                          #/Database
               ConnectionString: "..."         #/Database/ConnectionString
               MaxRetries: 3                   #/Database/MaxRetries
             FeatureFlags/                      #/FeatureFlags
               EnableNewCheckout: true         #/FeatureFlags/EnableNewCheckout

headline  →  "openapi.json | api-spec | 45 KB | 1.2k ln | ~12k tok | OpenAPI 3.0.2 | 47 paths, 23 schemas"
structure →  GET    /users              → 200: UserList       #/paths/~1users/get
             POST   /users              → 201: User           #/paths/~1users/post
             GET    /users/{id}         → 200: User           #/paths/~1users~1{id}/get
             schemas/User              → {id, name, email}   #/components/schemas/User

headline  →  "user.schema.json | schema | 3.4 KB | 95 ln | ~800 tok | draft-07 | User, Address, PhoneNumber"
structure →  User                                             #/definitions/User
               name: string (required)
               email: string, format:email (required)
               address: → Address
               phones: → PhoneNumber[]
             Address                                          #/definitions/Address
               street: string, city: string, zip: string
```

- An agent should be able to choose its depth: existence (headline scan), relevance (explore), structure (key paths), or content (read specific pointers)
- An agent should be able to navigate from headline to structure to specific content without re-querying, using JSON Pointer fragments as stable addresses
- An agent should be able to read a single key path without paying for the whole file
- An agent should be able to see a file's complete key structure without truncation
- An agent should be able to see token estimates on subtrees in structure, so it can budget before reading

---

## Addressing

- An agent should be able to address any value in any JSON file using JSON Pointer: `file:///config.json#/database/host`
- An agent should be able to use the same JSON Pointer fragment in read, explore, and query
- An agent should be able to see JSON Pointer paths in structure output and use them directly for navigation
- An agent should be able to find the line numbers of a specific key for editing

```
read("file:///config.json#/database", 500)
→ returns the database subtree only
```

---

## Relationships

- An agent should be able to traverse `$ref` pointers across JSON files as graph edges
- An agent should be able to find all files that reference a given JSON Schema via `$schema`
- An agent should be able to find all files that conform to the same schema
- An agent should be able to see the dependency graph of a manifest file
- An agent should be able to find config values that affect specific code paths

```sql
-- What schemas are referenced across the project?
SELECT DISTINCT target.uri
FROM edge e
JOIN node target ON e.target_id = target.id
WHERE e.kind = 'REFERS_TO'
```

---

## Query Surface

- An agent should be able to query key structure across all JSON files through one generic view, regardless of kind
- An agent should be able to query domain-specific views for recognized formats with richer vocabulary
- An agent should be able to fall back to ad-hoc JSON parsing on artifact content when pre-indexed views don't answer the question
- An agent should be able to combine all three levels in one SQL statement
- An agent should be able to list all indexed JSON files with their detected kind, shape, and key count through an inventory macro
- An agent should be able to read a JSON file as a queryable table at query time, the way `csv_data()` works for CSV
- An agent should be able to preview the first N keys or records of a JSON file without reading the whole thing

```sql
-- Inventory: what JSON files exist?
SELECT * FROM json_files()

-- Generic: key structure across all JSON files
SELECT * FROM json_keys WHERE key = 'version'

-- Specific: domain views for recognized kinds
SELECT * FROM api_endpoints WHERE method = 'POST'

-- Query-time access: read JSON content as a table
SELECT * FROM json_data('file:///events.json') LIMIT 10

-- Ad-hoc: parse raw content for questions the views don't cover
SELECT json_extract(a.content, '$.version') AS version
FROM artifact a WHERE a.uri LIKE '%package.json'
```

---

## Data Files

A 50MB JSON file with 100,000 records is not a config file with 100,000 keys. It's a dataset.

- An agent should be able to see a data file's shape from its headline: record count, field names, approximate size
- An agent should be able to query into large JSON data files without loading them into context
- An agent should be able to see a sample of records in structure without paying for the whole file
- An agent should be able to treat JSONL and NDJSON as equivalent to JSON arrays for structural queries

```
headline  →  "events.json | data | 12 MB | 48k ln | ~150k tok | 23,847 records | id, type, timestamp, payload"
structure →  Fields: id (string), type (string), timestamp (string), payload (object)
             Sample: {"id": "evt_001", "type": "click", "timestamp": "2024-...", "payload": {...}}
```

---

## Graph Density

Not every JSON key should become a graph node. A 2,000-key lock file would bloat the graph without adding value. The Markdown format doesn't create a node per word; JSON shouldn't create a node per key.

- An agent should be able to query key structure without the graph containing a node for every key in every file
- An agent should be able to get full key tree detail from structure text and ad-hoc JSON parsing, even when the graph stores only significant nodes
- An agent should be able to trust that the graph indexes what matters: top-level keys, named definitions, endpoints, config sections
- An agent should be able to query the full key tree of any file through the generic view, whether that view reads from nodes or parses content at query time

The principle: index what agents search for. Store what agents navigate to. Leave the rest queryable through the content.

---

## Variants

JSON appears in several syntactic variants. An agent shouldn't need to care about the difference.

- An agent should be able to get the same structural indexing from JSONC (comments), JSON5 (trailing commas, unquoted keys), and strict JSON
- An agent should be able to trust that comments in JSONC files are stripped during parsing, not exposed as structure
- An agent should be able to find JSON content embedded in other formats (Markdown code blocks, string literals) through the same query surface
- An agent should be able to distinguish between a standalone `.json` file and JSON embedded in another format

---

## Failure

- An agent should be able to trust that a malformed JSON file never prevents other files from indexing
- An agent should be able to see which JSON files failed to parse and why
- An agent should be able to get partial structure from a file that partially parsed: valid top-level keys even if a nested value is broken
- An agent should be able to distinguish "this file has no structure" from "this file failed to parse"
- An agent should be able to trust that when a specific type detector fails, the file falls back to generic indexing rather than becoming opaque

---

## Security

- An agent should be able to see annotations on JSON values that look like secrets: API keys, connection strings, tokens, passwords
- An agent should be able to find all potential secrets across all JSON config files in one query
- An agent should be able to trust that secret detection produces annotations, not redaction: the content is unchanged, the warning is adjacent

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Every JSON file gets real structure, not plain text | The generic tier is the floor: no JSON file is opaque |
| Recognized formats get domain-aware structure | Endpoint signatures, type definitions, config sections: not just keys |
| New type detectors don't touch the generic tier | The ceiling rises without disturbing the floor |
| JSON kinds are distinguishable from headlines | 200 files become navigable categories |
| Any value is addressable by JSON Pointer | Stable, standard fragments in every URI |
| `$ref` and `$schema` are graph edges | Schema relationships are traversable, not buried in strings |
| Key structure is queryable without graph bloat | The right detail level for the graph; the rest queryable from content |
| Data files are datasets, not documents | Shape in the headline, records queryable at scale |
| Malformed files fall back, never cascade | One broken file never blocks the rest |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Fall through to plain text for unknown JSON | An agent should get key tree structure from any JSON file |
| Require a specific detector before JSON is useful | An agent should navigate any JSON file from day one |
| Create a graph node for every key in every file | An agent should be able to query key structure without graph bloat |
| Treat all `.json` as one format | An agent should see the kind from its headline |
| Index lock files deeply | An agent should see a headline with package count; structure is noise |
| Show raw JSON as structure | An agent should see the file's essence, not its syntax tree |
| Let a specific detector failure make a file opaque | An agent should get generic structure as a fallback |
| Ignore `$ref` and `$schema` | An agent should traverse cross-references as graph edges |
| Silently store secrets in artifact content | An agent should see annotations on values that look like credentials |

---

*If an agent can't tell what a JSON file is from its headline, classification is broken. If it can't navigate by pointer, addressing is broken. If an unknown JSON file is opaque, the generic tier is broken. Test each independently.*
