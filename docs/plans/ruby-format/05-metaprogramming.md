---
description: Plan for Ruby format — attr_accessor extraction, Rails associations/validations/callbacks, delegate/scope patterns, and metaprogramming honesty annotations
tags: [format, ruby, plan, metaprogramming, rails, annotations]
audience: { human: 35, agent: 65 }
purpose: { plan: 95, design: 5 }
---

# Plan: Ruby — Metaprogramming and Framework Patterns

Implements: [Ruby Format Design](../../designs/current/ruby-format.md) — Metaprogramming (Recognizable Patterns), Graph Materialization (rb.property nodes, ASSOCIATES edges, annotations), SQL Views (ruby_associations, ruby_validations, ruby_callbacks, ruby_metaprogramming), X-Ray Summaries (generated method symbols)

## Scope

**Covers:**
- `rb.property` node materialization for attr_reader/writer/accessor
- Generated `rb.member` nodes for attr_reader (getter), attr_writer (setter), attr_accessor (both)
- Generated `rb.member` nodes for `delegate` and `scope` patterns
- Generated `rb.member` nodes for `define_method` with literal name
- ASSOCIATES edges for `has_many`, `belongs_to`, `has_one`
- `ruby.validation` annotations for `validates`
- `ruby.callback` annotations for `before_action`, `after_action`
- `ruby.metaprogramming` annotations for unextractable patterns (dynamic `define_method`, `class_eval`, `module_eval`, `method_missing`)
- SQL views: `ruby_associations`, `ruby_validations`, `ruby_callbacks`, `ruby_metaprogramming`
- X-ray structure updates: `~` visibility symbol for generated methods, association and validation lines
- Tests: each metaprogramming pattern, honesty annotations, generated method properties

**Does not cover:**
- ERB template support (extension point — separate loader)
- Gemspec/Gemfile dependency extraction (extension point)
- RBS type annotation linking (extension point)
- Framework-specific convenience views like `rails_models`, `rails_controllers` (extension point)

## Enables

Once this exists:
- **Rails models are queryable** — `SELECT * FROM ruby_associations WHERE model_name = 'User'` shows has_many/belongs_to/has_one
- **Validation rules visible** — `SELECT * FROM ruby_validations` shows what fields are validated and how
- **Callbacks traceable** — `SELECT * FROM ruby_callbacks WHERE callback_type = 'before_action'`
- **Generated methods appear in the graph** — `attr_accessor :name` produces `rb.member` nodes that appear in `ruby_methods` with `is_generated: true`
- **Agents know what's missing** — `SELECT * FROM ruby_metaprogramming WHERE document_uri LIKE '%user%'` shows where the graph is incomplete and why
- **X-ray shows the full picture** — structure includes associations, validations, and generated accessors alongside hand-written methods

This is the highest-value Ruby-specific increment. A Ruby indexer that ignores Rails misses the questions agents actually ask.

## Prerequisites

- Plan 02 complete — `rb.type` and `rb.member` nodes exist, materialization pipeline operational
- Plan 03 complete — mixin context helps distinguish module-level metaprogramming from class-level (not strictly required, but reduces rework)
- `RubyTreeSitterClient` extracts attr_accessor and dynamic method patterns (Plan 01 core queries), and provides extensible query execution for additional call patterns
- **This plan extends the tree-sitter client's query set** for framework-specific calls: `delegate`, `scope`, `has_many`, `belongs_to`, `has_one`, `validates`, `before_action`, `after_action`. These use the client's extensible query execution (Plan 01), not modifications to the core query set

## North Star

An agent querying a Rails model sees the complete API surface: hand-written methods, generated accessors, associations, validations, and callbacks. When the graph can't capture something — dynamic `define_method`, `class_eval`, `method_missing` — the agent knows it and knows why. No silent gaps.

## Done Criteria

### Attribute Accessors
- When `attr_reader :name` is encountered, the materializer shall create one `rb.property` node with `accessor_type: "reader"` and one generated `rb.member` node (getter) with `is_generated: "true"`, `generator: "attr_reader"`, `kind: "method"`, `is_static: "false"`
- When `attr_writer :name` is encountered, the materializer shall create one `rb.property` node with `accessor_type: "writer"` and one generated `rb.member` node (setter) with `is_generated: "true"`, `generator: "attr_writer"`
- When `attr_accessor :name` is encountered, the materializer shall create one `rb.property` node with `accessor_type: "accessor"` and two generated `rb.member` nodes (getter + setter) with `generator: "attr_accessor"`
- When multiple symbols are passed (e.g., `attr_accessor :name, :email`), nodes shall be created for each
- Generated `rb.member` nodes shall have `HAS_PART` composition edges from the enclosing `rb.type`
- Generated method visibility shall follow the current visibility state (attr_accessor after `private` produces private accessors)

### Delegate and Scope
- When `delegate :method_name, to: :target` is encountered, the materializer shall create a generated `rb.member` node with `is_generated: "true"`, `generator: "delegate"`, `delegate_to` prop
- When multiple methods are delegated (`delegate :foo, :bar, to: :baz`), a node shall be created for each
- When `scope :name, -> { ... }` is encountered, the materializer shall create a generated `rb.member` node with `is_generated: "true"`, `generator: "scope"`, `is_static: "true"`, `kind: "method"`

### define_method
- When `define_method(:name)` or `define_method('name')` is encountered with a literal argument, the materializer shall create an `rb.member` node with `is_generated: "true"`, `generator: "define_method"`
- When `define_method(variable)` is encountered with a non-literal argument, no method node shall be created (see Honesty below)

### Associations
- When `has_many :posts` is encountered, the materializer shall create an ASSOCIATES edge from the `rb.type` with props: `association: "has_many"`, `target: "posts"`
- When `belongs_to :user` is encountered, the materializer shall create an ASSOCIATES edge with `association: "belongs_to"`, `target: "user"`
- When `has_one :profile` is encountered, the materializer shall create an ASSOCIATES edge with `association: "has_one"`, `target: "profile"`
- ASSOCIATES edges shall be reference edges: `IsComposition = false`, `DstId = null`

### Annotations — Validations
- When `validates :field, ...` is encountered, the materializer shall create an annotation with `kind: "ruby.validation"`, `rule_id` set to the field name, `message` describing the validation rule
- The annotation's `scope_document_id` shall reference the containing document node
- When options are present (e.g., `presence: true, uniqueness: true`), they shall be stored in `data.options`

### Annotations — Callbacks
- When `before_action :method_name` is encountered, the materializer shall create an annotation with `kind: "ruby.callback"`, `rule_id: "before_action"`, `message` set to the method name
- When `after_action :method_name` is encountered, the materializer shall create an annotation with `kind: "ruby.callback"`, `rule_id: "after_action"`, `message` set to the method name
- When options are present (e.g., `only: [:create, :update]`), they shall be stored in `data.options`

### Honesty — Unextractable Metaprogramming
- When `define_method` with a non-literal argument is encountered, the materializer shall create an annotation with `kind: "ruby.metaprogramming"`, message: "dynamic method definition detected, name not extractable"
- When `class_eval` is encountered, the materializer shall create an annotation with message: "class_eval detected, definitions not extractable"
- When `module_eval` is encountered, the materializer shall create an annotation with message: "module_eval detected, definitions not extractable"
- When `instance_eval` is encountered, the materializer shall create an annotation with message: "instance_eval detected, definitions not extractable"
- When `method_missing` is defined, the materializer shall create an annotation with message: "method_missing defined, dynamic dispatch possible"
- Each annotation shall have a `target_span_id` pointing to the span of the metaprogramming call
- `eval(...)` without class_eval/module_eval/instance_eval prefix shall NOT be annotated

### SQL Views
- `ruby_associations` shall show: model_uri, model_name, model_qualified_name, association_type, target_model
- `ruby_validations` shall show: document_uri, field_name, validation_rule, options
- `ruby_callbacks` shall show: document_uri, callback_type, callback_method, options
- `ruby_metaprogramming` shall show: document_uri, description, line (from span)

### X-Ray Structure Updates
- Generated methods from attr_accessor shall appear in structure with `~` prefix (e.g., `~name (attr_accessor)`)
- Associations shall appear in structure (e.g., `has_many :posts`)
- Validations shall appear in structure (e.g., `validates :email`)
- The structure shall indicate generated vs hand-written at a glance

## Constraints

- **Syntactic extraction only** — patterns are matched by tree-sitter queries against source syntax. No framework version detection, no runtime interpretation. `has_many :posts` is extracted identically whether it's Rails, Sequel, or a custom DSL
- **Confidence is documented, not computed** — the design documents confidence levels (High/Medium/Low) for each pattern. The implementation does not store confidence on individual nodes — the design table is the reference
- **No annotation for bare `eval(...)`** — too common in non-structural contexts (IRB, scripts, test helpers). Only `class_eval`, `module_eval`, and `instance_eval` are annotated
- **Generated methods inherit visibility state** — if `attr_accessor` appears after `private`, the generated methods are private. Same rule as hand-written methods

## References

- [Ruby Format Design](../../designs/current/ruby-format.md) — Metaprogramming (Recognizable Patterns), Graph Materialization, SQL Views, X-Ray Summaries
- [Ruby North Star](../../north-star/formats/ruby.md) — honesty contract for metaprogramming
- [Testing Guidelines](../../knowledge/testing-guidelines.md) — TUnit, AwesomeAssertions, FakeItEasy

## Error Policy

| Failure | Behavior |
|---------|----------|
| attr_accessor with no arguments | Skip, log diagnostic |
| Association target is not a symbol literal | Skip edge, log diagnostic |
| Validates field is not a symbol literal | Skip annotation, log diagnostic |
| Callback method is not a symbol literal | Skip annotation, log diagnostic |
| Unrecognized metaprogramming pattern | Ignore silently — conservative by design |
| Recognized pattern is malformed (e.g., delegate without `to:`) | Skip, log diagnostic |
