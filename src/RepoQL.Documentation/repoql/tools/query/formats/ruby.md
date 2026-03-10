---
description: "ruby_types → classes and modules. ruby_methods → methods with visibility and generation info. ruby_mixins → include/prepend/extend. ruby_associations → has_many/belongs_to/has_one. ruby_metaprogramming → honesty annotations for unextractable patterns."
tags: ["ruby", "rails", "code", "mixins", "metaprogramming", "associations"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# Ruby Format

Query Ruby classes, modules, methods, mixins, constants, associations, and metaprogramming patterns with SQL views. Syntactic extraction via tree-sitter — no runtime, no framework detection.

---

## Capsule: RubyTypes

**Invariant**
`ruby_types` aggregates class and module definitions by qualified name, merging open-class reopenings.

**Example**
```sql
-- All types
SELECT qualified_name, type_kind, extends, definition_count
FROM ruby_types;

-- Classes with inheritance
SELECT qualified_name, extends
FROM ruby_types
WHERE type_kind = 'class' AND extends IS NOT NULL;

-- Types defined across multiple files (open classes)
SELECT qualified_name, defined_in
FROM ruby_types
WHERE definition_count > 1;
```
//BOUNDARY: `ruby_types` groups by `qualified_name`. For per-file type nodes, query `node` directly with `kind = 'rb.type'`. Reopening detection is within-file only — cross-file reopenings produce separate `definition_count` entries with the same `qualified_name`.

**Depth**
- `qualified_name`: Full `Module::Class` name
- `type_kind`: `class` or `module`
- `extends`: Superclass name (classes only)
- `definition_count`: Number of `rb.type` nodes with this qualified name
- `defined_in`: Array of document URIs (origin first)
- `file_uri`: First non-reopening definition
- `structure`: X-ray structure text
- Also participates in shared `Types` view via `WHERE n.kind LIKE '%.type'`

---

## Capsule: RubyMethods

**Invariant**
`ruby_methods` shows all methods with their enclosing type, visibility, and generation metadata.

**Example**
```sql
-- Public methods on a class
SELECT name, parameters
FROM ruby_methods
WHERE type_qualified_name = 'UserService' AND visibility = 'public';

-- Generated methods (attr_accessor, delegate, scope, define_method)
SELECT name, generator, type_qualified_name
FROM ruby_methods
WHERE is_generated = true;

-- Class methods (def self.method_name)
SELECT name, type_qualified_name
FROM ruby_methods
WHERE is_class_method = true;
```
//BOUNDARY: Top-level functions (outside any class/module) are `rb.function` nodes, not in `ruby_methods`. Query `node WHERE kind = 'rb.function'` or use the shared `Functions` view.

**Depth**
- `visibility`: `public`, `protected`, or `private` (tracks Ruby's visibility state machine)
- `is_class_method`: `true` for `def self.x` and singleton methods on `self`
- `is_generated`: `true` for methods created by `attr_accessor`, `attr_reader`, `attr_writer`, `delegate`, `scope`, `define_method`
- `generator`: Source pattern name (e.g., `attr_accessor`, `delegate`, `scope`)
- `accepts_block`: `true` when method signature includes `&block`
- `parameters`: Raw parameter text from source
- Also participates in shared `Functions` view (`rb.member` and `rb.function` kinds)

---

## Capsule: RubyMixins

**Invariant**
`ruby_mixins` and `ruby_mro` expose include/prepend/extend relationships with method resolution ordering.

**Example**
```sql
-- What modules does a class include?
SELECT module_name, mechanism
FROM ruby_mixins
WHERE type_qualified_name = 'User';

-- Method resolution order (Ruby MRO)
SELECT module_name, mechanism, mro_tier, mixin_order
FROM ruby_mro
WHERE type_qualified_name = 'User';

-- Find all types that include a module
SELECT type_qualified_name, type_kind
FROM ruby_mixins
WHERE module_name = 'Serializable';
```
//BOUNDARY: MRO tiers: 0 = PREPENDS, 1 = INCLUDES, 2 = EXTENDS_MODULE. `extend self` produces an EXTENDS_MODULE edge with null target.

**Depth**
- `mechanism`: Edge type — `INCLUDES`, `PREPENDS`, or `EXTENDS_MODULE`
- `mixin_order`: Declaration order within the type
- `mro_tier`: Numeric tier for resolution precedence
- `ruby_inheritance` view shows `EXTENDS` (superclass) relationships separately

---

## Capsule: RubyAssociations

**Invariant**
`ruby_associations` surfaces `has_many`, `belongs_to`, and `has_one` declarations as queryable rows.

**Example**
```sql
-- All associations for a model
SELECT association_type, target_model
FROM ruby_associations
WHERE model_qualified_name = 'User';

-- Find all belongs_to relationships
SELECT model_name, target_model
FROM ruby_associations
WHERE association_type = 'belongs_to';

-- Relationship graph
SELECT model_name, association_type, target_model
FROM ruby_associations
ORDER BY model_name;
```
//BOUNDARY: Syntactic extraction only. `has_many :posts` is extracted identically whether it's Rails, Sequel, or a custom DSL. No framework detection.

---

## Capsule: RubyAnnotations

**Invariant**
`ruby_validations` and `ruby_callbacks` expose Rails-style declarations. `ruby_metaprogramming` flags patterns the graph cannot fully represent.

**Example**
```sql
-- Validations on a model
SELECT field_name, validation_rule, options
FROM ruby_validations
WHERE file_uri LIKE '%user%';

-- Callbacks
SELECT callback_type, callback_method, options
FROM ruby_callbacks
WHERE file_uri LIKE '%controller%';

-- Where is the graph incomplete?
SELECT file_uri, description, line
FROM ruby_metaprogramming;
```
//BOUNDARY: Metaprogramming annotations cover: dynamic `define_method`, `class_eval`, `module_eval`, `instance_eval`, `method_missing`. Bare `eval(...)` is NOT annotated.

**Depth**
- Validations: `field_name` from first symbol arg, `validation_rule` is the full declaration text, `options` captures keyword args
- Callbacks: `callback_type` is `before_action` or `after_action`, `callback_method` is the target method name
- Metaprogramming: each annotation has a `line` from the source span — use `snippet()` to see context

---

## Capsule: RubyDependencies

**Invariant**
`ruby_requires`, `ruby_constants`, and `ruby_aliases` map the namespace and dependency graph.

**Example**
```sql
-- External gems vs internal requires
SELECT required_path, dependency_type
FROM ruby_requires
WHERE file_uri LIKE '%app/models%';

-- Constants defined in a namespace
SELECT name, qualified_name
FROM ruby_constants
WHERE namespace = 'Config';

-- Method aliases
SELECT alias_name, original_name, alias_type
FROM ruby_aliases;
```
//BOUNDARY: `ruby_requires` captures `require` and `require_relative`. `is_internal` is `true` for `require_relative`. Gem resolution is not performed — paths are stored as written in source.

---

## Views

```sql
ruby_types(qualified_name, type_kind, extends, definition_count, defined_in, file_uri, structure)
ruby_methods(file_uri, type_uri, type_name, type_qualified_name, method_uri, headline, name, visibility, is_class_method, accepts_block, is_generated, generator, parameters)
ruby_mixins(type_uri, type_name, type_qualified_name, type_kind, mechanism, module_name, mixin_order)
ruby_mro(type_uri, type_name, type_qualified_name, module_name, mechanism, mro_tier, mixin_order)
ruby_inheritance(class_uri, class_name, qualified_name, superclass_name)
ruby_constants(file_uri, namespace, constant_uri, name, qualified_name)
ruby_requires(file_uri, required_path, is_internal, dependency_type)
ruby_aliases(source_uri, alias_name, alias_type, original_name, original_uri)
ruby_associations(model_uri, model_name, model_qualified_name, association_type, target_model)
ruby_validations(file_uri, field_name, validation_rule, options)
ruby_callbacks(file_uri, callback_type, callback_method, options)
ruby_metaprogramming(file_uri, description, line)
```

---

## Node Kinds

- `rb.type` — Class or module (distinguished by `properties->>'kind'`: `class` or `module`)
- `rb.member` — Method within a type (distinguished by `properties->>'kind'`: `method` or `singleton_method`)
- `rb.function` — Top-level function (outside any class or module)
- `rb.constant` — Constant assignment (`FOO = ...`)
- `rb.property` — Attribute accessor declaration (`attr_reader`, `attr_writer`, `attr_accessor`)

## Edge Types

- `HAS_PART` — Composition (document -> type -> member/constant/property)
- `EXTENDS` — Class inheritance (`class Foo < Bar`)
- `INCLUDES` — Module inclusion (`include Mod`)
- `PREPENDS` — Module prepending (`prepend Mod`)
- `EXTENDS_MODULE` — Module extension (`extend Mod`)
- `REQUIRES` — Dependency (`require 'lib'`, `require_relative 'file'`)
- `ALIASES` — Method aliasing (`alias new_name old_name`)
- `ASSOCIATES` — Model association (`has_many`, `belongs_to`, `has_one`)

---

## File Extensions

| Extension / Name | Media Type Kind |
|------------------|-----------------|
| `.rb` | `code.ruby` |
| `.rake`, `Rakefile` | `code.ruby.rake` |
| `.gemspec` | `code.ruby.gemspec` |
| `Gemfile` | `code.ruby.gemfile` |
| `Guardfile`, `Dangerfile` | `code.ruby` |
| `.erb` | Not supported |

---

## Common Patterns

| Goal | Query |
|------|-------|
| Find all Ruby files | `SELECT uri, headline FROM node WHERE kind = 'document' AND properties->>'language' = 'ruby'` |
| List classes | `SELECT qualified_name, extends FROM ruby_types WHERE type_kind = 'class'` |
| List modules | `SELECT qualified_name FROM ruby_types WHERE type_kind = 'module'` |
| Methods on a class | `SELECT name, visibility FROM ruby_methods WHERE type_qualified_name = 'MyClass'` |
| Generated accessors | `SELECT name, generator FROM ruby_methods WHERE is_generated = true` |
| Rails model associations | `SELECT association_type, target_model FROM ruby_associations WHERE model_name = 'User'` |
| Inheritance tree | `SELECT qualified_name, superclass_name FROM ruby_inheritance` |
| Mixin graph | `SELECT type_qualified_name, mechanism, module_name FROM ruby_mixins` |
| External dependencies | `SELECT required_path FROM ruby_requires WHERE dependency_type = 'external'` |
| Graph completeness | `SELECT file_uri, description FROM ruby_metaprogramming` |
| View structure without reading | `SELECT headline, structure FROM artifact a JOIN node n ON n.artifact_id = a.id WHERE n.uri = '...'` |

---

## Common Mistakes

| Mistake | Fix |
|---------|-----|
| Looking for `rb.class` or `rb.module` node kinds | Both are `rb.type` — filter by `properties->>'kind'` |
| Looking for `rb.method` node kind | Methods are `rb.member` — filter by `properties->>'kind'` for method vs singleton_method |
| Expecting top-level functions in `ruby_methods` | Top-level functions are `rb.function` nodes — use shared `Functions` view or query `node` directly |
| Expecting `return_type` values | Ruby has no static types — `return_type` is always null |
| Assuming cross-file reopening detection | Reopening is within-file only — same qualified name in different files produces separate type nodes with same `qualified_name` in `ruby_types` |
| Querying `ruby_associations` for framework-specific behavior | Extraction is syntactic — `has_many :posts` is matched regardless of framework |
| Missing `eval(...)` in `ruby_metaprogramming` | Only `class_eval`, `module_eval`, `instance_eval` are annotated — bare `eval` is excluded |
