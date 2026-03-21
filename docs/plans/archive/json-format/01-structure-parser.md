---
description: Plan for JSON format — core streaming parser producing key trees, shape detection, JSON Pointer paths, and line numbers
tags: [format, json, plan, parser, structure]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: JSON — Structure Parser

Implements: [JSON Format Design](../../designs/json-format.md) — JsonStructureParser section

## Scope

**Covers:**
- New project `RepoQL.Formats.Json` (scaffold only — no pipeline integration yet)
- New test project `RepoQL.Formats.Json.Tests`
- `JsonStructureParser` — streaming parser producing `JsonParseResult`
- `JsonParseResult`, `JsonKeyInfo`, `JsonShape`, `JsonParseOptions` types
- UTF-8 byte encoding, line-offset table, binary search line resolution
- JSON Pointer path construction state machine
- Shape detection (flat object, nested object, array, single value, empty)
- Large file sampling (root arrays, JSONL detection by content shape)
- Subtree token estimation
- Node selection heuristic (depth + MaxNodes cap)
- Solution file and `Directory.Packages.props` updates (if needed)

**Does not cover:**
- Classification or media types (Plan: 02-generic-json-tier)
- Pipeline integration, DI registration (Plan: 02-generic-json-tier)
- Materialization to graph Records (Plan: 02-generic-json-tier)
- Liquid templates, SQL macros (Plan: 02-generic-json-tier)
- JSONC/JSON5 normalization (Plan: 03-jsonc-support)
- Secret detection (Plan: 04-secret-detection)

## Enables

Once this exists:
- **Plan 02 can proceed** — `JsonLoader` consumes `JsonStructureParser.Parse()` directly
- **Plan 03 can start** — `JsonNormalizer` is a peer utility that operates on the same UTF-8 byte array; the byte-span overload `Parse(ReadOnlySpan<byte>)` is its integration point
- **Key tree correctness is validated** — all downstream plans assume the parser produces correct paths, line numbers, and shape. Testing this in isolation is simpler and faster than testing through the pipeline
- **Future specific handlers have a foundation** — OpenAPI, JSON Schema, package.json parsers will compose with `JsonStructureParser`

This is the foundation increment. Every subsequent plan assumes the parser works.

## Prerequisites

- .NET 10 SDK (solution already targets this)
- No external dependencies — `System.Text.Json` ships with .NET

## North Star

Parse any JSON file. Get back a key tree with pointer addresses, line numbers, shape, and token estimates. Never allocate a DOM. A 50MB data file takes the same memory as a 5KB config. When the file is malformed, throw a clear exception — the loader (Plan 02) decides how to handle it.

## Done Criteria

### Project Structure
- The project `RepoQL.Formats.Json` shall build targeting .NET 10
- The project shall reference `RepoQL.Contracts` (for shared types only — no pipeline dependency yet)
- The test project `RepoQL.Formats.Json.Tests` shall reference TUnit, AwesomeAssertions
- Both projects shall be included in `RepoQL.sln`

### Parse Method
- `JsonStructureParser.Parse(string text, JsonParseOptions? options)` shall accept a JSON string and return `JsonParseResult`
- The parser shall encode the input string to UTF-8 bytes once, using this byte array for both `Utf8JsonReader` and the line-offset table
- An overload `Parse(ReadOnlySpan<byte> utf8Bytes, JsonParseOptions? options)` shall accept pre-encoded UTF-8 bytes directly, for use by `JsonNormalizer` (Plan 03) which operates at the byte level
- When text is null, the parser shall throw `ArgumentNullException`
- When text is empty or whitespace, the parser shall return a result with `Shape = Empty` and no keys

### Key Tree
- The parser shall produce a `JsonKeyInfo` for each property in the JSON document
- Each key shall include:
  - `Path` — JSON Pointer (e.g., `/database/host`)
  - `Name` — last path segment (e.g., `host`)
  - `Depth` — nesting level (root properties = 0)
  - `ValueKind` — from `System.Text.Json.JsonValueKind`
  - `StartLine` and `EndLine` — 1-based line numbers
  - `EstimatedTokens` — subtree byte span / 4
  - `ScalarValue` — for string/number/bool values, truncated to 100 chars
  - `ArrayLength` — for array values, element count

### Path Construction
- The parser shall build JSON Pointer paths using a stack-based state machine
- When a property value is a scalar, the parser shall emit the key and pop the path segment
- When a property value is a container, the parser shall push on PropertyName and pop on EndObject/EndArray
- When inside an array, the parser shall track element indices manually (0-based)
- The path `/data/0/name` shall be produced for the `name` key of the first element in the `data` array
- Paths shall use JSON Pointer escaping: `~0` for `~`, `~1` for `/`

### Line Number Resolution
- The parser shall scan the UTF-8 byte array for `0x0A` bytes, recording byte offset of each line start
- The parser shall resolve `TokenStartIndex` to line number via binary search into the line-offset table
- Line numbers shall be 1-based (first line = 1)
- For files with `\r\n` line endings, line boundaries shall be determined by `0x0A` only
- For files containing multi-byte UTF-8 characters, line numbers shall be correct (byte-domain consistency)

### Shape Detection
- When all root values are scalars, shape shall be `FlatObject`
- When any root value is an object or array, shape shall be `NestedObject`
- When the root is an array, shape shall be `Array`
- When the root is a single scalar, shape shall be `SingleValue`
- `TotalKeyCount` shall reflect the total number of properties encountered during parsing (for sampled files, this is the count from the sampled portion only — not a full-file count)

### Large File Sampling
- When the root is an array, the parser shall read the first `MaxSampleRecords` (default: 100) elements and stop
- The parser shall estimate total array length from file size and average sample record size
- `ArrayLength` on the result shall reflect the estimate (not null) when sampling was applied
- When the root is not an array, the parser shall read the full document

### JSONL / NDJSON
- When `JsonParseOptions.IsJsonl` is true, the parser shall treat the input as newline-delimited JSON (one JSON value per line)
- The parser shall split the input on `\n` boundaries and parse each line as a separate JSON value using `Utf8JsonReader`
- The parser shall sample the first `MaxSampleRecords` lines, same as array sampling
- Shape shall be `Array` for JSONL input
- `ArrayLength` shall be estimated from file size and average line length
- Keys shall be extracted from sampled records, as if each line were an array element
- The caller (`JsonLoader`) is responsible for setting `IsJsonl` based on file extension (`.jsonl`, `.ndjson`)
- When a sampled line fails to parse, the parser shall skip it and continue (JSONL files often have a mix of valid and invalid lines)

### Subtree Token Estimation
- For container values, the parser shall record the byte offset at the opening token and read forward to the matching close
- `EstimatedTokens` shall be `(endByte - startByte) / 4`
- For scalar values, `EstimatedTokens` shall be based on the value's byte length

### Node Selection Heuristic
- A key is node-eligible if: `(depth < MaxNodeDepth) OR (depth >= MaxNodeDepth AND value is a container)`, subject to the `MaxNodes` cap
- `JsonParseOptions.MaxNodeDepth` (default: 2) — keys at depth 0 and 1 are always node-eligible; keys at depth 2+ are node-eligible only if they are containers (objects or arrays)
- `JsonParseOptions.MaxNodes` (default: 200) — once this many node-eligible keys have been found, no further keys are node-eligible
- Keys that exceed the cap still appear in the result (for structure text rendering) but with `IsNodeEligible = false`
- Each `JsonKeyInfo` shall carry an `IsNodeEligible` boolean reflecting these rules

### Error Handling
- When the JSON is malformed, the parser shall throw `JsonException` with the original message
- The parser shall not attempt partial parse recovery — that is the loader's responsibility (Plan 02)

## Constraints

- **No pipeline dependency** — the parser is a standalone utility. It does not reference `RepoQL.Indexing` or any pipeline types. This keeps it composable for specific handlers
- **No DOM allocation** — `Utf8JsonReader` only. The parser never creates a `JsonDocument` or `JsonElement`
- **System.Text.Json only** — no Newtonsoft, no third-party JSON libraries
- **Byte-domain consistency** — the line-offset table and `Utf8JsonReader` must operate on the same `byte[]`. Do not mix character offsets with byte offsets

## References

- [JSON Format Design](../../designs/json-format.md) — `JsonStructureParser` section, path construction, line number tracking, node selection heuristic
- [JSON North Star](../../north-star/json.md) — progressive disclosure, addressing, key structure vision
- [`Utf8JsonReader` docs](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/use-utf8jsonreader) — `TokenStartIndex`, streaming API
- [JSON Pointer (RFC 6901)](https://datatracker.ietf.org/doc/html/rfc6901) — path syntax, escaping rules
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions
- [Processor Guide](../../../src/Indexing/RepoQL.Indexing/PROCESSOR_GUIDE.md) — project structure conventions

## Error Policy

The parser throws on malformed JSON. It does not log, annotate, or recover. The caller (JsonLoader in Plan 02) is responsible for catching `JsonException` and producing appropriate diagnostics.

This keeps the parser simple and testable — its contract is: valid JSON in, structured result out; invalid JSON in, exception out.
