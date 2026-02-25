---
description: "rust_derives(type_uri, type_name, type_qualified_name, derived_trait)"
tags: ["query", "views", "rust", "derives", "derive", "macros"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Views[95%]"]
---

# Rust Derives View

Derive macro applications (`DERIVES` edges) — one row per type x derived trait combination.

## Quick Reference

```sql
-- Most commonly derived traits
SELECT derived_trait, COUNT(*) AS count
FROM rust_derives
GROUP BY derived_trait
ORDER BY count DESC;

-- What does a type derive?
SELECT derived_trait FROM rust_derives WHERE type_name = 'Config';

-- Find all Serialize types
SELECT type_name FROM rust_derives WHERE derived_trait = 'Serialize';
```

---

## Capsule: RustDerivesVsProperty

**Invariant**
`rust_derives` has one row per derive instance. `rust_types.derives` has the same data as a comma-separated string. Use `rust_derives` for filtering and aggregation. Use `rust_types.derives` for display.

**Example**
```sql
-- Per-trait filtering: use rust_derives
SELECT type_name
FROM rust_derives
WHERE derived_trait = 'Clone';

-- Display: use rust_types.derives
SELECT name, derives
FROM rust_types
WHERE type_kind = 'struct';

-- Derive fingerprinting (trait combination analysis)
SELECT type_name, LIST(derived_trait ORDER BY derived_trait) AS fingerprint
FROM rust_derives
GROUP BY type_name
ORDER BY LEN(fingerprint) DESC;
```
//BOUNDARY: The column is `derived_trait`, NOT `trait_name`. A common mistake is using `trait_name` which does not exist in this view.

**Depth**
- `derived_trait`: The trait name from `#[derive(Trait)]` (e.g., `Debug`, `Clone`, `Serialize`)
- Each `#[derive(Debug, Clone)]` produces two rows — one for `Debug`, one for `Clone`
- Derive fingerprinting reveals architectural patterns: `[Debug]` = internal, `[Clone, Debug, Eq, PartialEq]` = value types
- SeeAlso: `rust_impls` for explicit trait implementations (vs derived)

---

## Capsule: RustDerivesAggregation

**Invariant**
The one-row-per-derive structure enables powerful aggregation and cross-referencing.

**Example**
```sql
-- Types that derive both Clone and Copy
SELECT type_name
FROM rust_derives
WHERE derived_trait IN ('Clone', 'Copy')
GROUP BY type_name
HAVING COUNT(DISTINCT derived_trait) = 2;

-- Derive count per type
SELECT type_name, COUNT(*) AS derive_count
FROM rust_derives
GROUP BY type_name
ORDER BY derive_count DESC;

-- Cross-reference: types that derive Serialize but not Deserialize
SELECT d1.type_name
FROM rust_derives d1
LEFT JOIN rust_derives d2
  ON d1.type_name = d2.type_name AND d2.derived_trait = 'Deserialize'
WHERE d1.derived_trait = 'Serialize'
  AND d2.type_name IS NULL;
```

---

## Column Reference

| Column | Type | Description |
|--------|------|-------------|
| `type_uri` | string | URI of the type node |
| `type_name` | string | Simple name of the type |
| `type_qualified_name` | string | Qualified name of the type |
| `derived_trait` | string | Name of the derived trait (e.g., `Debug`, `Clone`) |
