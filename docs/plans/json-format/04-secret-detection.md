---
description: Plan for JSON format — secret detection analyzer producing annotations on credential-looking values
tags: [format, json, plan, analyzer, secrets, annotations]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: JSON — Secret Detection

Implements: [JSON Format Design](../../designs/json-format.md) — Secret Detection section

## Scope

**Covers:**
- `JsonSecretDetector` — `IFormatAnalyzer` implementation
- `SecretPatterns` — pattern definitions (key name patterns, value prefix patterns)
- Registration in the JSON `FormatDescriptor`'s analyzer slot (replacing the stub from Plan 02)
- Annotations with `kind = "lint"`, `severity = "warning"`, `rule_id = "json.potential-secret"`
- Line number resolution for annotation spans
- Tests for key-name matching, value-prefix matching, false positive resistance

**Does not cover:**
- `$ref` validation (multi-file analysis — future plan, not part of JSON format initial delivery)
- Cross-file secret deduplication
- Secret redaction or masking — annotations are warnings, not transformations
- AppSettingsLoader's existing secret scanning (it stays independent)

## Enables

Once this exists:
- **Agents see potential secrets** — `SELECT * FROM annotation WHERE rule_id = 'json.potential-secret'` finds every credential-looking value across all JSON files
- **Proactive security** — explore output includes warning annotations next to suspicious values
- **Consistency** — the same secret patterns apply to all JSON files, not just appsettings

## Prerequisites

- Plan 02 (Generic JSON Tier) complete — the `FormatDescriptor` must exist with an analyzer slot, and JSON files must flow through the pipeline

## North Star

An agent scans a repository and sees annotations on every JSON value that looks like a credential. No false negatives for common patterns (API keys, connection strings, bearer tokens). Low false positive rate — warnings are useful, not noisy. The agent finds all potential secrets in one query without opening any files.

## Done Criteria

### JsonSecretDetector
- `JsonSecretDetector` shall implement `IFormatAnalyzer`
- The analyzer shall examine `DocumentModel.Text` (the original file content)
- The analyzer shall use `JsonStructureParser`'s parse result (from `DocumentModel.Metadata`) for key paths and line numbers
- The analyzer shall apply both key-name patterns and value patterns

### Key-Name Patterns
- The analyzer shall flag values whose key name contains (case-insensitive): `secret`, `password`, `passwd`, `token`, `apikey`, `api_key`, `api-key`, `connectionstring`, `connection_string`, `connection-string`
- The analyzer shall NOT flag keys that merely contain substrings of these (e.g., `description` contains `secret` as a false match is acceptable since severity is warning — but `password_hint` should be flagged)
- When a flagged key has a non-empty string value, the analyzer shall produce an annotation

### Value Patterns
- The analyzer shall flag string values starting with known credential prefixes: `sk-`, `pk-`, `ghp_`, `gho_`, `github_pat_`, `Bearer `, `Basic `, `xox`
- The analyzer shall flag string values that look like base64-encoded secrets: length > 20 characters, matches `^[A-Za-z0-9+/=]{20,}$`
- The analyzer shall NOT flag values that are clearly non-secret despite matching patterns:
  - Empty strings
  - Placeholder values: strings containing `TODO`, `CHANGEME`, `<`, `>`, `{`, `}`
  - Very short strings (< 8 characters) for value-pattern matches

### Annotation Output
- Each annotation shall have `kind = "lint"`
- Each annotation shall have `severity = "warning"`
- Each annotation shall have `rule_id = "json.potential-secret"`
- Each annotation shall have a `message` describing what was detected (e.g., "Key 'password' may contain a secret")
- Each annotation shall reference the `StartLine` of the key/value in the original file
- Each annotation shall include the key path in the message (e.g., `/database/connectionString`)

### Registration
- `JsonSecretDetector` shall be registered as a singleton in `JsonServiceCollectionExtensions.AddJsonFormat()`
- The `FormatDescriptor`'s analyzer slot (currently `null` from Plan 02) shall be updated to reference `JsonSecretDetector`
- The analyzer shall run during the single-file analysis pipeline phase

### Overlap with AppSettingsLoader
- When `appsettings*.json` files are handled by `AppSettingsLoader` (not `JsonLoader`), the `JsonSecretDetector` shall NOT run on them (since the FormatDescriptor only applies to files handled by its loader)
- This overlap is naturally avoided by the `CanLoadAsync` exclusion from Plan 02

### Tests
- A JSON file with a key named `password` and a non-empty value shall produce one annotation
- A JSON file with a key named `api_key` containing `sk-abc123...` shall produce one annotation
- A JSON file with a value starting with `ghp_` shall produce one annotation (value pattern, regardless of key name)
- A JSON file with a key named `description` shall NOT produce an annotation (even though it contains `secret` — wait, it doesn't. Test that `description` is clean)
- A JSON file with `password` key and an empty value shall NOT produce an annotation
- A JSON file with `password` key and value `<your-password-here>` shall NOT produce an annotation (placeholder)
- A JSON file with no secret-like content shall produce zero annotations
- Annotations shall have correct `StartLine` values matching the key's position in the file

## Constraints

- **Annotations, not redaction** — the analyzer produces warnings. It does not modify the artifact content. The design explicitly states "annotations, not redaction: the content is unchanged, the warning is adjacent"
- **Conservative patterns** — prefer fewer false positives over catching every edge case. `severity = "warning"` means agents treat these as hints. A noisy analyzer trains agents to ignore it
- **Single-file scope** — the analyzer examines one file at a time. Cross-file analysis (e.g., "same secret appears in 3 files") is out of scope
- **Same rule_id for all patterns** — all detections use `json.potential-secret`. Do not create separate rule_ids for key-name vs value-pattern matches

## References

- [JSON Format Design](../../designs/json-format.md) — Secret Detection section
- [JSON North Star](../../north-star/json.md) — Security section
- `src/Formats/RepoQL.Formats.DotNet/AppSettingsAnalyzer.cs` — existing secret scanning approach (reference for pattern ideas)
- `src/RepoQL.Contracts/IFormatAnalyzer.cs` — analyzer interface
- `src/RepoQL.Contracts/Models/Annotation.cs` — annotation model
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions

## Error Policy

The analyzer must never prevent a file from being indexed. If pattern matching throws an unexpected exception:
1. Log warning with file URI and exception
2. Return empty annotation array
3. The file proceeds through the pipeline with no secret annotations

A failed analyzer is invisible to the agent — the file simply has no security warnings. This is acceptable because false silence is less harmful than blocking indexing.
