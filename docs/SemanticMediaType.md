---
description: Spec for semantic media types encoding format and representation via parameters like kind, profile, schema, version.
documentationCategory: primer
tags: [repoql, media-type, mime, semtype, json, yaml, markdown]
---

## Spec: Semantic Media Type (SemType)

**Goal.** A media type string that encodes both the **data format** and what the payload **represents**, without breaking MIME.

### 1. Syntax

```
semtype = type "/" subtype [ "+" suffix ] *( ";" OWS param )
param   = token [ "=" ( token / quoted-string ) ]
```

* Grammar is compatible with RFC 6838 (media types) and RFC 7230 (tokens).
* `type`, `subtype`, `suffix`, and parameter keys are lowercase.
* Exactly one `suffix` MAY appear (e.g., `+json`, `+xml`).

### 2. Reserved parameters

Keys are lowercase. Unknown keys MUST be preserved.

* `kind` = token
  What the data represents. Dot‑notation suggested. Examples: `openapi`, `cs.class`, `py.module`, `markdown.doc`, `playwright.trace`, `zip.entry`.

* `profile` = quoted-string (absolute URI)
  A URI identifying the profile/vocabulary that constrains semantics (aligned with RFC 6906).

* `schema` = quoted-string (absolute URI)
  A URI for a validating schema/IDL (JSON Schema, Avro, Proto, etc).

* `version` = token
  Representation version label (semantic version or domain version).

* `charset` = token
  Standard parameter for text encodings.

### 3. Normalization

* Lowercase `type`, `subtype`, `suffix`, and parameter keys.
* Sort parameters by key when rendering.
* Quote parameter values that are not valid HTTP tokens; escape `\` and `"` inside quotes.
* URIs in `profile` and `schema` SHOULD be absolute and MAY be quoted.

### 4. Semantics

* **Format vs representation**: `type/subtype[+suffix]` conveys the wire format. `kind` says what the bytes represent.
* **Backwards compatible**: If consumers ignore `kind`/`profile`/`schema`, the string remains a valid media type.
* **Open set**: Additional parameters MAY be defined by producers.

### 5. Producer guidance

* Choose the closest registered media type and suffix (`application/json`, `text/markdown`, `application/zip`, etc).
* Set `kind` using a short, stable dot‑path that matches your domain model (e.g., your graph node kinds).
* Use `profile` to anchor semantics to a document. Use `schema` for validation artifacts.
* Include `version` when the representation’s contract changes.

### 6. Examples

| Payload              | SemType                                                                              |
| -------------------- | ------------------------------------------------------------------------------------ |
| Markdown doc         | `text/markdown; kind=markdown.doc; charset=utf-8`                                    |
| JSON config + schema | `application/json; kind=config.app; schema="file:///schemas/app.schema.json"`        |
| OpenAPI YAML         | `application/yaml; kind=openapi; version=3.1; profile="https://example.org/oas/3.1"` |
| C# class file        | `text/x-csharp; kind=cs.class; charset=utf-8`                                        |
| Python module        | `text/x-python; kind=py.module; charset=utf-8`                                       |
| Playwright trace ZIP | `application/zip; kind=playwright.trace`                                             |

### 7. Parsing algorithm (brief)

1. Split at the first `;` outside quotes → head and params.
2. In head, split at `/` → `type` and `subtype[+suffix]`. If `+` exists, last `+` defines `suffix`.
3. Parse parameters as `k[=v]`, honoring quoted strings with escapes. Lowercase keys.
4. Expose `kind`, `profile`, `schema`, `version`, `charset` as conveniences.

### 8. Rendering algorithm (brief)

1. Emit `type "/" subtype [ "+" suffix ]`.
2. Append parameters sorted by key as `;key` or `;key=value`. Quote values that are not tokens.

This standard gives you a single string that states both **how** to parse bytes and **what** those bytes mean, while remaining valid MIME.
