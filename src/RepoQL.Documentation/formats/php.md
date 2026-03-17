---
description: "PHP — classes, interfaces, traits, enums, methods, properties, inheritance, trait usage — php_types, php_members, php_inheritance, php_trait_usage views"
tags: ["php", "format", "indexing", "types", "traits", "enums"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Formats[100%]"]
---

# PHP Format

Query PHP classes, interfaces, traits, enums, methods, properties, constants, and inheritance with SQL views. Syntactic extraction via tree-sitter — no PHP runtime required.

---

## Capsule: PhpTypes

**Invariant**
`php_types` aggregates class, interface, trait, and enum declarations with modifiers, inheritance, and X-ray structure.

**Example**
```sql
-- All types with their kind
SELECT name, type_kind, accessibility
FROM php_types;

-- Abstract classes
SELECT name, extends, implements
FROM php_types
WHERE type_kind = 'class' AND is_abstract = true;

-- Final classes
SELECT name, file_uri
FROM php_types
WHERE is_final = true;

-- Enums with backing types
SELECT name, backed_type
FROM php_types
WHERE type_kind = 'enum';
```
//BOUNDARY: `type_kind` is one of: `class`, `interface`, `trait`, `enum`. All four share the same `php.type` node kind. `extends` and `implements` are stored as comma-separated strings when multiple values exist.

**Depth**
- `type_uri`: Addressable URI (use with `read` or `snippet`)
- `qualified_name`: `Namespace\ClassName` format
- `is_abstract` / `is_final`: Class modifiers (always false for interfaces, traits, enums)
- `extends`: Superclass name (classes) or comma-separated parent interfaces (interfaces)
- `implements`: Comma-separated interface names (classes and enums)
- `backed_type`: Enum backing type (`string` or `int`, null for pure enums)
- `structure`: X-ray structure text
- Also participates in shared `Types` view via `WHERE n.kind LIKE '%.type'`

---

## Capsule: PhpMembers

**Invariant**
`php_members` shows methods, properties, constants, and enum cases belonging to a type.

**Example**
```sql
-- Methods on a class
SELECT name, accessibility, return_type
FROM php_members
WHERE type_name = 'UserService' AND member_kind = 'method';

-- Static methods
SELECT type_name, name, return_type
FROM php_members
WHERE is_static = true AND member_kind = 'method';

-- Properties with types
SELECT type_name, name, type, accessibility
FROM php_members
WHERE member_kind = 'property';

-- Enum cases
SELECT type_name, name
FROM php_members
WHERE member_kind = 'enum_case';

-- Constants
SELECT type_name, name
FROM php_members
WHERE member_kind = 'constant';
```
//BOUNDARY: `member_kind` values: `method`, `property`, `constant`, `enum_case`. Top-level functions (outside any class/interface/trait/enum) are `php.function` nodes, not in `php_members` — query `node WHERE kind = 'php.function'` or use the shared `Functions` view.

**Depth**
- `member_uri`: Addressable URI for the member
- `type_name` / `type_uri`: Enclosing type
- `accessibility`: `public`, `protected`, or `private`
- `is_static`: True for static methods and properties
- `return_type`: Declared return type (null if untyped)
- `type`: Property type declaration (null if untyped)
- `headline`: One-line summary

---

## Capsule: PhpInheritance

**Invariant**
`php_inheritance` shows all inheritance relationships — extends, implements, and trait usage — in a single view.

**Example**
```sql
-- What does a class extend?
SELECT target_name
FROM php_inheritance
WHERE source_name = 'UserController' AND relationship = 'EXTENDS';

-- What interfaces does a class implement?
SELECT target_name
FROM php_inheritance
WHERE source_name = 'UserService' AND relationship = 'IMPLEMENTS';

-- All trait usage
SELECT source_name, target_name
FROM php_inheritance
WHERE relationship = 'USES_TRAIT';

-- Full inheritance graph
SELECT source_name, relationship, target_name
FROM php_inheritance
ORDER BY source_name;
```
//BOUNDARY: `relationship` is one of: `EXTENDS`, `IMPLEMENTS`, `USES_TRAIT`. This view unifies all three edge types. For trait usage specifically, `php_trait_usage` provides a focused alternative.

**Depth**
- `source_uri`: URI of the type declaring the relationship
- `source_name`: Name of the type
- `source_kind`: `class`, `interface`, `trait`, or `enum`
- `target_name`: Name of the target type (not resolved to a URI — syntactic extraction only)

---

## Capsule: PhpTraitUsage

**Invariant**
`php_trait_usage` shows which types use which traits — a focused subset of `php_inheritance`.

**Example**
```sql
-- Which traits does a class use?
SELECT trait_name
FROM php_trait_usage
WHERE type_name = 'UserController';

-- Who uses a trait?
SELECT type_name, type_kind
FROM php_trait_usage
WHERE trait_name = 'Loggable';

-- Trait usage by type kind
SELECT type_kind, COUNT(*) AS usage_count
FROM php_trait_usage
GROUP BY type_kind;
```
//BOUNDARY: Only shows `USES_TRAIT` edges. For the full inheritance picture (extends, implements, traits), use `php_inheritance`.

---

## Views

```sql
php_types(file_uri, type_uri, headline, name, qualified_name, type_kind, accessibility, is_abstract, is_final, extends, implements, backed_type, structure)
php_members(file_uri, type_uri, type_name, member_uri, headline, name, member_kind, accessibility, is_static, return_type, type)
php_inheritance(source_uri, source_name, source_kind, relationship, target_name)
php_trait_usage(type_uri, type_name, type_kind, trait_name)
```

---

## Node Kinds

- `php.type` — Class, interface, trait, or enum (distinguished by `properties->>'kind'`: `class`, `interface`, `trait`, `enum`)
- `php.member` — Method within a type
- `php.property` — Property declaration
- `php.constant` — Class/interface constant
- `php.enum_case` — Enum case
- `php.function` — Top-level function (outside any type)
- `php.namespace` — Namespace declaration
- `php.use` — Use/import statement

## Edge Types

- `HAS_PART` — Composition (document -> type -> member/property/constant/enum_case)
- `EXTENDS` — Class inheritance (`class Foo extends Bar`) or interface extension
- `IMPLEMENTS` — Interface implementation (`class Foo implements Bar`)
- `USES_TRAIT` — Trait usage (`use SomeTrait`)

---

## File Extensions

| Extension | Media Type Kind |
|-----------|-----------------|
| `.php` | `code.php` |
| `.phtml` | `code.php.template` |
| `.php3`, `.php4`, `.php5`, `.php7` | `code.php` |
| `.phps` | `code.php` |
| `.inc` | `code.php` |

---

## Common Patterns

| Goal | Query |
|------|-------|
| Find all PHP files | `SELECT uri, headline FROM Files WHERE lang = 'php'` |
| List classes | `SELECT name, extends, implements FROM php_types WHERE type_kind = 'class'` |
| List interfaces | `SELECT name FROM php_types WHERE type_kind = 'interface'` |
| List traits | `SELECT name FROM php_types WHERE type_kind = 'trait'` |
| List enums | `SELECT name, backed_type FROM php_types WHERE type_kind = 'enum'` |
| Methods on a class | `SELECT name, accessibility, return_type FROM php_members WHERE type_name = 'MyClass' AND member_kind = 'method'` |
| Public API surface | `SELECT type_name, name FROM php_members WHERE accessibility = 'public' AND member_kind = 'method'` |
| Inheritance tree | `SELECT source_name, relationship, target_name FROM php_inheritance` |
| Trait usage | `SELECT type_name, trait_name FROM php_trait_usage` |
| Abstract classes | `SELECT name FROM php_types WHERE is_abstract = true` |
| Static methods | `SELECT type_name, name FROM php_members WHERE is_static = true AND member_kind = 'method'` |
| View structure without reading | `SELECT headline, structure FROM artifact a JOIN node n ON n.artifact_id = a.id WHERE n.uri = '...'` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Looking for `php.class` or `php.interface` node kinds | All types are `php.type` — filter by `properties->>'kind'` or use `php_types.type_kind` |
| Looking for `php.method` node kind | Methods are `php.member` — use `php_members` view with `member_kind = 'method'` |
| Expecting top-level functions in `php_members` | Top-level functions are `php.function` nodes — use shared `Functions` view or query `node` directly |
| Using `properties->>'kind'` in WHERE clauses | Use `json_extract_string(properties, '$.kind')` in WHERE/CASE to avoid DuckDB type coercion errors |
| Expecting runtime type information | Extraction is syntactic (tree-sitter) — no PHP runtime, no composer, no type inference |
| Expecting resolved trait targets | Trait names are stored as written in source — no namespace resolution is performed |
| Confusing `php_inheritance` and `php_trait_usage` | `php_trait_usage` is a focused view for `USES_TRAIT` edges only; `php_inheritance` includes extends, implements, and trait usage |
