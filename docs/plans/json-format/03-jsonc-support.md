---
description: Plan for JSON format — JSONC comment stripping via space replacement for zero-offset-delta normalization
tags: [format, json, jsonc, plan, normalizer]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: JSON — JSONC Support

Implements: [JSON Format Design](../../designs/json-format.md) — JSONC / JSON5 Support section

## Scope

**Covers:**
- `JsonNormalizer` — byte-level comment scanner that replaces comment bytes with `0x20`
- Integration with `JsonLoader.LoadAsync` — normalize `.jsonc` files before parsing, fallback normalization for `.json` files that fail strict parsing
- Tests for comment stripping, string-awareness, line number correctness, multi-byte characters

**Does not cover:**
- JSON5 normalization (trailing commas, unquoted keys, single-quote strings, hex numbers, multiline strings) — deferred; see design trade-offs
- `JsonStructureParser` changes (none needed — it receives valid JSON after normalization)
- Pipeline changes (classification already claims `.jsonc`; parsing already delegates to `JsonLoader`)

## Enables

Once this exists:
- **`.jsonc` files get real structure** — `tsconfig.json`, VS Code `settings.json`, and other JSONC files are parsed correctly instead of failing with `JsonException`
- **`.json` files with comments work too** — real-world `.json` files that contain comments (common in VS Code configs) get structural indexing via the fallback path instead of `PipelineResult.Error`
- **Line numbers are automatically correct** — byte-level space replacement preserves byte count and `0x0A` positions, so the parser's existing line-offset table works unchanged
- **`read("file:///tsconfig.json")` returns original text** — comments visible
- **`read("file:///tsconfig.json#/compilerOptions")` returns logical subtree** — comments stripped, valid JSON

## Prerequisites

- Plan 01 (JsonStructureParser) complete — the parser must work for strict JSON before we feed it normalized JSONC
- Plan 02 (Generic JSON Tier) complete — `JsonLoader.LoadAsync` is the integration point

## North Star

A `.jsonc` file with comments is indistinguishable from a strict `.json` file in the graph — same key tree, same headlines, same queryability. The only visible difference: `read()` returns the original text with comments. No source maps. No offset translation. No API changes. Just spaces where comments were.

## Done Criteria

### JsonNormalizer

- `JsonNormalizer.StripComments(byte[] utf8Bytes)` shall replace comment bytes in-place with `0x20` (space)
- The normalizer shall operate on UTF-8 bytes, not C# strings — multi-byte UTF-8 characters in comments must have each byte individually replaced with `0x20` to preserve total byte count
- The normalizer shall replace line comment bytes (`//` through end of line, exclusive of `0x0A`) with `0x20`
- The normalizer shall replace block comment bytes (`/*` through `*/`, inclusive of delimiters) with `0x20`
- Within block comments, `0x0A` (LF) and `0x0D` (CR) bytes shall be preserved — only non-newline bytes are replaced
- The byte array length shall be unchanged (in-place mutation, no reallocation)
- All `0x0A` bytes shall be at the **same positions** in the array after normalization
- When the input contains no comments, no bytes shall be modified
- A convenience overload `StripComments(string text)` shall encode to UTF-8, normalize, and return the normalized `byte[]` for callers that start with a string

### Why Byte-Level Space Replacement

The design document describes a source map approach (normalized offset → original offset → line number). Byte-level space replacement eliminates the source map entirely:
- Same byte count means `Utf8JsonReader.TokenStartIndex` values are valid in both original and normalized byte arrays
- Same `0x0A` positions means the parser's existing line-offset table produces correct line numbers
- `JsonStructureParser.Parse(ReadOnlySpan<byte>)` (the byte overload from Plan 01) receives the normalized bytes directly — no intermediate string
- `DocumentModel.Text` stores original text (string); the normalized bytes are an internal detail of `LoadAsync`

Operating on bytes (not strings) is essential because a multi-byte UTF-8 character (e.g., `é` = 2 bytes) inside a comment must become 2 space bytes (`0x20 0x20`), not 1. A string-level replacement would produce a different UTF-8 byte count, breaking `TokenStartIndex` alignment.

This is a design simplification discovered during planning. The design's source map approach is the fallback if space replacement proves insufficient for future JSON5 support (where normalization changes byte length).

### String Awareness

- The normalizer shall NOT replace `//` or `/*` byte sequences that appear inside JSON string values
- The normalizer shall track whether the scanner is inside a string by detecting unescaped `0x22` (`"`) bytes
- The normalizer shall handle escaped quotes (`\"`) inside strings — these do not end the string
- The normalizer shall handle escaped backslashes (`\\`) before quotes — `\\"` ends the string
- When a string contains `// comment-like text`, the string bytes shall be preserved exactly
- String detection operates on ASCII bytes (`0x22`, `0x5C`) which are unambiguous in UTF-8 — no multi-byte character contains these byte values

### Integration with JsonLoader

**`.jsonc` files (explicit):**
- When the file extension is `.jsonc`, `JsonLoader.CanLoadAsync` shall return true (this plan enables it — Plan 02 excluded `.jsonc`)
- `LoadAsync` shall encode the text to UTF-8 bytes, call `JsonNormalizer.StripComments(byte[])`, then pass the normalized bytes to `JsonStructureParser.Parse(ReadOnlySpan<byte>)`
- `DocumentModel.Text` shall contain the **original** text (with comments)
- `JsonStructureParser` receives normalized bytes via the byte-span overload from Plan 01 — no string round-trip

**`.json` files with comments (fallback):**
- When a `.json` file fails `JsonStructureParser.Parse(string)` with `JsonException`, `LoadAsync` shall attempt recovery:
  1. Encode the text to UTF-8 bytes
  2. Call `JsonNormalizer.StripComments(byte[])` on the bytes
  3. Call `JsonStructureParser.Parse(ReadOnlySpan<byte>)` with the normalized bytes
  4. If the normalized bytes parse successfully, proceed with the result
  5. If normalization also fails, re-throw the original `JsonException`
- This handles real-world `.json` files that contain comments (VS Code `settings.json`, some `tsconfig.json` files) without requiring them to be renamed to `.jsonc`
- The fallback path is only taken after a parse failure — strict `.json` files never pay the normalization cost

### Edge Cases

- When a block comment is unterminated at end of file, the normalizer shall replace from `/*` to EOF with `0x20` and return (the resulting bytes will likely fail JSON parsing, which `JsonParser` handles per standard error policy)
- When a line comment is on the same line as JSON content (`"key": "value" // comment`), only the comment bytes (`// comment`) shall be replaced with `0x20`
- When the byte array starts with a UTF-8 BOM (`0xEF 0xBB 0xBF`), the normalizer shall skip over it (BOM bytes are not comment syntax)
- Multi-byte UTF-8 characters in comments are handled naturally — each byte is individually replaced with `0x20`, preserving total byte count. No character-level logic needed

### Tests

- Strict JSON (no comments) shall leave all bytes unchanged
- Line comments shall be replaced with `0x20`, keys on the same line shall have correct line numbers
- Block comments shall be replaced with `0x20`, keys after the comment shall have correct line numbers
- Multi-line block comments shall not corrupt line numbering for subsequent keys
- Strings containing `//` and `/*` byte sequences shall be preserved exactly
- Strings containing `\"` and `\\\"` shall be handled correctly
- Normalized byte array shall have identical length to input (verify for every test case)
- `0x0A` byte positions in output shall match input (verify for every test case with block comments)
- Multi-byte UTF-8 characters in comments shall each become `0x20`, preserving byte count
- A `.jsonc` file indexed through the pipeline shall produce the same key tree as the equivalent strict JSON file (same keys, same paths, same line numbers)
- A `.json` file containing comments shall succeed via the fallback path and produce the same key tree as the equivalent strict JSON file

## Constraints

- **Byte-level operation** — the normalizer operates on `byte[]`, not `string`. Multi-byte UTF-8 characters in comments require byte-by-byte replacement to preserve total byte count. String-level replacement would produce wrong UTF-8 byte lengths
- **String-aware scanner, not regex** — the normalizer must track string boundaries by detecting `0x22` (`"`) bytes. Regex cannot distinguish `//` inside a string from an actual comment. Must handle escaped quotes
- **JSONC only, not JSON5** — this plan handles comments only. Trailing commas, unquoted keys, single-quote strings are out of scope. JSON5 changes byte length (adding quotes around bare keys, etc.) and would require the source map approach from the design document
- **Space replacement, not removal** — comment bytes become `0x20`. This preserves byte count and `0x0A` positions, eliminating the need for source maps or `JsonStructureParser` API changes
- **Original text in DocumentModel.Text** — the artifact stores the file as the user wrote it. The normalized bytes are internal to `LoadAsync`
- **No changes to JsonStructureParser** — Plan 01's byte-span overload `Parse(ReadOnlySpan<byte>)` is used directly. No new API needed

## References

- [JSON Format Design](../../designs/json-format.md) — JSONC / JSON5 Support section, source map approach (superseded by space replacement for JSONC, retained for future JSON5)
- [JSON North Star](../../north-star/json.md) — Variants section
- JSONC — subset of JSON with `//` and `/* */` comments; no formal spec, widely adopted (VS Code, TypeScript, `tsconfig.json`)
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions

## Error Policy

A JSONC file with an unterminated block comment is not fatal. The normalizer replaces what it can (`/*` through EOF becomes spaces). The resulting text may or may not parse as valid JSON:
- If it parses: the file gets structure normally
- If it doesn't parse: `JsonParser` handles the `JsonException` per standard error policy (log, return `PipelineResult.Error`)

Never let a comment-stripping edge case make a file invisible. Partial results beat no results.
