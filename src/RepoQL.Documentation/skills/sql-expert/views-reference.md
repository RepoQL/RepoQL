---
description: "Quick reference for RepoQL SQL views: Files, Types, Functions, Annotations, and FileSystems."
tags: ["skill", "sql-expert", "views", "files", "types", "functions", "annotations"]
audience: ["LLMs"]
categories: ["Skill[100%]"]
---

# Views Quick Reference

Views are the designed SQL interface. Start here, not with base tables.

---

## Files

Document inventory. Pre-joins node + artifact + annotation counts.

| Column | Key use |
|--------|---------|
| `uri` | Full RepoQL URI (`file:///path`, `help:///path`, `github://...`) |
| `path` | Path without scheme |
| `lang` | Semantic media type (e.g., `code.csharp`, `markdown.doc`) |
| `lines` | Line count |
| `headline` | One-line X-ray summary |
| `summary` | Brief X-ray overview |
| `structure` | Detailed X-ray TOC |
| `error_count` | Lint error count |
| `warning_count` | Lint warning count |

Full schema: `help:///repoql/tools/query/views/files.md`

---

## Types

Classes, interfaces, structs, enums — sub-document type declarations.

| Column | Key use |
|--------|---------|
| `name` | Type name |
| `type_kind` | `class`, `interface`, `struct`, `enum`, etc. |
| `extends` | Base type name |
| `implements` | Comma-separated interface list |
| `file_uri` | Containing file URI |

Full schema: `help:///repoql/tools/query/views/types.md`

---

## Functions

Methods, constructors, functions — callable members.

| Column | Key use |
|--------|---------|
| `name` | Function/method name |
| `signature` | Full signature string |
| `declaring_type` | Containing type name |
| `is_async` | Async flag |
| `return_type` | Return type string |
| `file_uri` | Containing file URI |

Full schema: `help:///repoql/tools/query/views/functions.md`

---

## Annotations

Diagnostics, lint results, metrics — out-of-band facts attached to code.

| Column | Key use |
|--------|---------|
| `severity` | `error`, `warning`, `info`, `hint` |
| `rule_id` | Lint rule identifier |
| `message` | Human-readable description |
| `resolved_target_uri` | URI of the annotated entity |
| `kind` | `lint`, `metric`, `outline`, `hint` |

Full schema: `help:///repoql/tools/query/views/annotations.md`

---

## FileSystems

Mounted file systems and their status.

Full schema: `help:///repoql/tools/query/views/filesystems.md`

---

*Views cover 90% of needs. Base tables are there when views aren't enough.*
