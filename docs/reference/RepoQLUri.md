---
description: Specification for RepoURI addressing scheme, fragments, normalization, and parsing/rendering algorithms.
documentationCategory: comprehensive
tags: [repoql, uri, repository-uri, rfc3986, json-pointer, anchors, line-range, char-range, duckdb, sql]
---

# Repository URI Specification (RepoURI 1.0)

This document defines an interpretable URI form for addressing files and sub‑resources inside files.
A RepoURI is a standard absolute URI **without** a fragment (the **container**) plus an optional **fragment** that encodes sub‑resource location.

```
repo-uri = container-uri [ "#" fragment ]
container-uri = absolute-URI  ; RFC 3986, no fragment component
```

Normative keywords **MUST**, **SHOULD**, **MAY** are used as in RFC 2119.

---

## 1. Components

### 1.1 Container URI

* **Definition**: Absolute URI that identifies the file-like container.
  Examples: `file:///repo/README.md`, `https://host/path/spec.yaml`.
* **Rules**

    * MUST be absolute.
    * MUST NOT include a fragment.
    * MAY include a query.
    * For local files use `file:///…` with normalized path segments.
    * Case normalization: scheme and authority lowercase; path case preserved.

### 1.2 Archive containers

* **Definition**: Address an entry within an archive using the `jar:` form.
* **Syntax**

  ```
  jar-uri = "jar:" container-uri "!" "/" entry-path *( "!" "/" entry-path )
  entry-path = 1*( unreserved / pct-encoded / sub-delims / ":" / "@" )
  ```

  Examples
  `jar:file:///artifacts/trace.zip!/resources/network.log`
  Nested: `jar:file:///a.zip!/b.zip!/c.txt`
* **Rules**

    * Each `entry-path` is UTF‑8 then percent‑encoded.
    * The archive URI itself is the **container-uri**.

---

## 2. Fragments (sub‑resource location)

Exactly one fragment MAY be present. Four forms exist. Parsers MUST apply them in this precedence order:

1. **JSON Pointer** (starts with `/`)
2. **Parameterized** (`k=v` pairs joined by `&`)
3. **Simple range** (`line=` or `char=` prefix)
4. **Anchor** (plain slug)

### 2.1 JSON Pointer

* **Syntax**

  ```
  fragment = "/" *( json-char )
  json-char = pchar / "/"
  ```
* **Semantics**: RFC 6901 pointer into a JSON/YAML-like tree. First slash is required.
* **Examples**

    * `file:///api/openapi.yaml#/components/schemas/User`
    * `file:///cfg.json#/databases/0/name`

### 2.2 Parameterized fragment

* **Syntax**

  ```
  fragment = param *( "&" param )
  param = key [ "=" value ]
  key   = 1*( ALPHA / DIGIT / "_" / "-" )
  value = *pchar
  ```
* **Reserved keys**

    * `symbol=<qualname>`: language symbol identifier. Value MUST be percent‑decoded by consumers.
    * `line=a[,b]` : 1‑based inclusive lines. `a` or `b` MAY be omitted.
    * `char=a[,b]` : 0‑based character offsets, half‑open `[a,b)`. `a` or `b` MAY be omitted.
* **Unknown keys** MUST be preserved and round‑tripped.
* **Examples**

    * `file:///src/lib.cs#symbol=Foo.Bar&line=12,20`
    * `file:///src/app.py#line=40`
    * `file:///src/file.txt#char=100,150`

### 2.3 Simple range

* **Syntax**

  ```
  fragment = "line=" number [ "," number ]
           / "char=" number [ "," number ]
  ```
* **Semantics**: Same as the `line` and `char` parameters above.

### 2.4 Anchor

* **Definition**: Opaque slug for headings or element ids.
* **Syntax**

  ```
  fragment = 1*( unreserved / pct-encoded / "-" / "_" / "." )
  ```
* **Producer guidance**: Slugging SHOULD be stable and ASCII. Recommended: lowercase, trim, spaces→`-`, collapse repeated `-`, strip most punctuation.

---

## 3. Normalization

* Container normalization:

    * Remove `.` and `..` segments.
    * Percent‑encode non‑ascii and reserved characters.
    * Preserve path case.
* Fragment normalization:

    * JSON Pointers MUST preserve escape rules (`~`→`~0`, `/`→`~1` per segment).
    * Parameterized fragments SHOULD sort keys lexicographically when rendering.
    * Anchors SHOULD be percent‑encoded as needed; case policy is producer‑defined.

---

## 4. Semantics

* **Locator, not identity**: RepoURI identifies a location. Canonical identity remains an internal UUID and/or content digest.
* **Open set**: New parameters MAY be added. Existing parsers MUST ignore unknown parameters.
* **Out‑of‑bounds**: Line and char ranges MAY exceed current file bounds; they still denote intended locations.
* **Mutual exclusivity**:

    * JSON Pointer MUST NOT be combined with other forms.
    * Simple range and parameterized forms MUST NOT be combined except that `symbol` MAY co‑exist with `line`/`char`.

---

## 5. Parsing algorithm (normative)

Given a `repo-uri` string:

1. Split at the first `#`. Left is `container-uri`. Right (if any) is raw `fragment`.
2. Validate `container-uri` is an absolute URI and has no fragment.
3. If `fragment` starts with `/`, treat as JSON Pointer. Do not parse parameters.
4. Else if `fragment` contains `=`, parse `&`‑separated `k=v` pairs:

    * Recognize `symbol`, `line`, `char`. Preserve others verbatim.
    * For `line`/`char`, split by comma into optional bounds; parse integers.
5. Else if `fragment` starts with `line=` or `char=`, parse as simple range.
6. Else treat as `anchor` (percent‑decode for display only).

---

## 6. Rendering algorithm (normative)

Given a container URI and structured location:

1. Start with `container-uri` (already normalized).
2. Determine fragment in priority:

    * If `jsonPointer` present → emit as is (ensure leading `/`).
    * Else if parameters present or `symbol` present → render sorted `k=v` pairs, including `line` and `char` when set.
    * Else if `line` or `char` set → render simple range.
    * Else if `anchor` set → emit anchor as given.
    * Else no fragment.
3. Join with `#`.

---

## 7. Examples

| Purpose              | RepoURI                                                             |
| -------------------- | ------------------------------------------------------------------- |
| Markdown anchor      | `file:///repo/README.md#installation`                               |
| Lines 40–55          | `file:///repo/README.md#line=40,55`                                 |
| Single line 12       | `file:///repo/app.py#line=12`                                       |
| Char offsets 100–150 | `file:///repo/file.txt#char=100,150`                                |
| JSON key             | `file:///repo/config.json#/servers/0/url`                           |
| OpenAPI schema       | `file:///api/openapi.yaml#/components/schemas/User`                 |
| Symbol at lines      | `file:///repo/lib.cs#symbol=Foo.Bar&line=12,20`                     |
| Archive entry        | `jar:file:///artifacts/trace.zip!/resources/network.log#line=1,200` |
| Nested archive       | `jar:file:///a.zip!/b.zip!/c.txt`                                   |

---

## 8. Validation

An implementation SHOULD expose:

* `Container` as a normalized absolute URI.
* Structured fragment fields: `Anchor`, `JsonPointer`, `Symbol`, `Line(start,end)`, `Char(start,end)`, and raw `Parameters`.
* Round‑trip guarantee: parsing then rendering MUST produce a byte‑identical URI when unknown parameters are preserved and normalization rules are applied.

---

## 9. Backward compatibility

* URIs without fragments remain valid.
* Unknown fragment text remains a valid anchor.
* Future fragment keys MUST NOT break existing parsers.

---

## 10. Security considerations

* Treat percent‑decoded paths as data. Do not execute or dereference without policy checks.
* Do not assume case behavior of the filesystem from the URI alone.
* Avoid path traversal by normalizing `container-uri` before access.
