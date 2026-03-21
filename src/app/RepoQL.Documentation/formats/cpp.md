---
description: "C/C++ format support: extracted structure, multi-file analysis, SQL views, and known boundaries."
tags: ["cpp", "c", "format", "indexing", "graph", "views"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Formats[100%]"]
---

# C/C++ Format

RepoQL indexes C and C++ source/header files with tree-sitter extraction plus cross-file graph completion.

## Indexed File Types

| Extension | Media Type Kind |
|-----------|-----------------|
| `.c` | `code.c` |
| `.cpp`, `.cc`, `.cxx` | `code.cpp` |
| `.h`, `.hpp`, `.hh`, `.hxx` | `code.cpp-header` |
| `.ipp`, `.tpp`, `.inl` | `code.cpp-inline` |

## Capabilities

- Types: classes, structs, unions, enums, concepts
- Members/functions: fields, methods, constructors, free functions
- Namespaces: nested, inline, anonymous
- Preprocessor: includes, macros, conditional-compilation annotations
- Template metadata: parameters and specializations
- Cross-file linking:
  - declaration -> definition (`REFERS_TO` with `relationship=defines`)
  - forward declaration -> full type (`REFERS_TO` with `relationship=forward_declares`)
  - inheritance completion (`EXTENDS` with `access` and optional `is_virtual`)
  - transitive include dependencies (`REFERS_TO` with `relationship=transitive_include`)

## SQL Views

### `cpp_classes`

Projection over `cpp.type` where `kind IN ('class','struct','union')`.

Columns:
- `uri`, `file_uri`
- `name`, `qualified_name`
- `type_kind`, `default_access`, `extends`, `is_abstract`
- `start_line`, `end_line`
- `headline`, `node_id`, `span_id`

### `cpp_functions`

Projection over `cpp.member` + `cpp.function` where node kind is method/constructor/function.

Columns:
- `uri`, `file_uri`
- `name`, `qualified_name`, `declaring_type`
- `return_type`, `access`, `signature`
- `is_virtual`, `is_pure_virtual`, `is_noexcept`, `is_constexpr`, `is_static`
- `start_line`, `end_line`
- `headline`, `node_id`, `span_id`

### `cpp_includes`

Projection over `cpp.include`.

Columns:
- `target_header`
- `include_style`
- `source_uri`
- `node_id`

### `cpp_templates`

Projection over `cpp.%` nodes where `is_template = 'true'`.

Columns:
- `uri`, `name`
- `template_params`
- `base_template`, `template_args`
- `template_kind` (`primary` or `specialization`)
- `file_uri`, `node_id`

### `cpp_enums`

Projection over enum nodes (`cpp.type` where `kind='enum'`).

Columns:
- `uri`, `name`
- `is_scoped`
- `underlying_type`
- `file_uri`, `node_id`

### `cpp_macro_invocations`

Projection over `annotation` with `rule_id='cpp/macro_interference'`.

Columns:
- `id`, `message`
- `name`, `context`
- `file_uri`
- `start_line`, `end_line`
- `span_id`

### `cpp_namespace_members`

Projection over `cpp.%` nodes with non-null `namespace`.

Columns:
- `namespace`, `name`
- `member_kind`
- `accessibility`
- `file_uri`, `node_id`

## Query Examples

Find classes in a namespace:

```sql
SELECT qualified_name, type_kind, extends
FROM cpp_classes
WHERE qualified_name LIKE 'net::%';
```

Trace inheritance edges:

```sql
SELECT
    d.properties->>'qualified_name' AS derived,
    b.properties->>'qualified_name' AS base,
    e.properties->>'access' AS access,
    COALESCE(e.properties->>'is_virtual', 'false') AS is_virtual
FROM edge e
JOIN node d ON d.id = e.source_node_id
JOIN node b ON b.id = e.destination_node_id
WHERE e.type = 'EXTENDS' AND d.kind = 'cpp.type' AND b.kind = 'cpp.type';
```

See all members contributed to a namespace across files:

```sql
SELECT namespace, name, member_kind, file_uri
FROM cpp_namespace_members
WHERE namespace = 'net::transport'
ORDER BY member_kind, name;
```

Find macro interference sites:

```sql
SELECT file_uri, start_line, name, context, message
FROM cpp_macro_invocations
ORDER BY file_uri, start_line;
```

## Preprocessor Boundary

RepoQL does not execute a full preprocessor or compiler type-resolution pass in the baseline C/C++ format loader.

What you can see:
- `#include` directives as include nodes/edges
- macro definitions and known macro-interference annotations
- conditional-compilation boundaries as annotations

What you cannot fully see without compiler enrichment:
- expanded macro bodies and generated declarations
- full overload/type resolution across translation units
- compile-configuration-dependent symbol visibility

## Known Limitations

- Header/source linking uses qualified name + arity (not full type-signature resolution)
- Ambiguous base-class matches are skipped with warnings
- System headers not indexed in the repository terminate include-chain resolution
- No libclang-backed semantic enrichment in baseline mode
