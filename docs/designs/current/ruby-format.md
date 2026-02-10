---
description: Design for Ruby format support — extracting classes, modules, methods, mixins, and relationships from Ruby source via tree-sitter
tags: [format, ruby, tree-sitter, design, code]
audience: { human: 45, agent: 55 }
purpose: { design: 85, flow: 15 }
---

# Ruby Format — Design

## North Star

An agent should understand a Ruby codebase's structure — classes, modules, mixins, methods, and their relationships — without reading source files, and query it all through the same SQL surface as every other format. When a class is reopened across three files, the agent sees one complete picture. When a module is mixed into nine models, the agent traverses that graph and traces the method resolution order.

**Informed by:** `docs/north-star/formats/ruby.md`
**Research:** `docs/research/ruby-parsing-from-dotnet.md`

## Context

Ruby files appear in repositories as application code, gems, scripts, Rakefiles, config files, and DSL hosts (Rails routes, RSpec specs, Gemfiles). Ruby's syntax is regular enough for structural extraction but has specific challenges: open classes (definitions distributed across files), mixin-based composition (include/extend/prepend), visibility that applies to subsequent definitions rather than individual declarations, and metaprogramming that creates methods at parse time (`attr_accessor`, `delegate`, `scope`).

The PHP format loader established the pattern for using a grammar-based parser (ANTLR4) in a code format. This design follows that pattern closely, substituting tree-sitter for ANTLR4. The loader/parser/materializer split, state transfer via `DocumentModel.Metadata`, and SQL view registration are identical.

**Key difference from PHP:** Ruby has open classes (one class, many files) and mixins (include/extend/prepend) instead of interfaces and traits. PHP's trait system is conceptually similar but syntactically different. Ruby also has widespread metaprogramming that generates structural declarations (`attr_accessor`, `has_many`, `validates`).

## Constraints

| Constraint | Source |
|------------|--------|
| Single writer to DuckDB | Hard constraint — all writes through `DuckDbDataStore` |
| Frozen schema — 5 tables only | Extend via views/macros/UDFs, never new tables |
| Errors never cascade | One malformed Ruby file must never stop indexing |
| TreeSitter.DotNet (MIT) | NuGet package with Ruby grammar bundled. Cross-platform native binaries for all six RepoQL targets |
| No Ruby runtime preferred | Parser runs in-process via native tree-sitter library. Not a hard constraint — research identifies subprocess approaches as viable fallbacks |
| tree-sitter parsers not thread-safe | Each thread needs its own Parser instance |

---

## Design

### Classification

Ruby files get provisional media type `text/x-ruby` from the naming convention layer (`.rb` extension). The classifier confirms and adds the kind parameter.

| Extension | Kind | Notes |
|-----------|------|-------|
| `.rb` | `code.ruby` | Standard Ruby source |
| `.rake` | `code.ruby.rake` | Rake task files |
| `.gemspec` | `code.ruby.gemspec` | Gem specifications |
| `Gemfile` | `code.ruby.gemfile` | Bundler dependency file |
| `Rakefile` | `code.ruby.rake` | Rake entry point |
| `Guardfile`, `Dangerfile` | `code.ruby` | DSL hosts — parse as standard Ruby |
| `.erb` | Not handled | ERB is a template format, not Ruby — separate loader (see Extension Points) |

```csharp
SemanticMediaType.Create("text", "x-ruby").WithKind("code.ruby")
```

### Tree-Sitter Integration

The core complexity is contained behind `RubyTreeSitterClient` — a thin wrapper around TreeSitter.DotNet that no other component touches. No tree-sitter types escape this class.

```
RubyTreeSitterClient
├── Parse(string sourceCode) → RubyParseResult
├── Thread-local Parser instances (tree-sitter is not thread-safe)
└── S-expression queries for symbol extraction
```

**Thread safety:** Tree-sitter parsers are not thread-safe. `RubyTreeSitterClient` uses `ThreadLocal<Parser>` to give each thread its own parser instance. The `Language` object is created once and shared — it is immutable and thread-safe.

**Query-based extraction:** Rather than walking the full CST, use tree-sitter's query language to target specific patterns. This is more robust than manual traversal — queries match structurally, not positionally.

```scheme
;; Classes (with superclass detection for reopening heuristic)
(class name: (constant) @class_name
       superclass: (superclass (scope_resolution)? @super)?)

;; Modules
(module name: (constant) @module_name)

;; Methods (with parameter details)
(method name: (identifier) @method_name
        parameters: (method_parameters)? @params)

;; Singleton methods (class methods, or methods on specific objects)
(singleton_method
    object: (_) @receiver
    name: (identifier) @method_name)

;; Singleton class blocks (class << self)
(singleton_class value: (_) @target)

;; Attribute accessors
(call method: (identifier) @call_name
      arguments: (argument_list) @args
 (#match? @call_name "^attr_(reader|writer|accessor)$"))

;; Include/extend/prepend (with ordinal from source position)
(call method: (identifier) @mixin_type
      arguments: (argument_list (constant) @module)
 (#match? @mixin_type "^(include|extend|prepend)$"))

;; Constants
(assignment left: (constant) @const_name)

;; Require statements
(call method: (identifier) @req_method
      arguments: (argument_list (string (string_content) @path))
 (#match? @req_method "^require(_relative)?$"))

;; Yield detection (within method bodies — indicates block acceptance)
(yield) @yield_site

;; Block parameters
(block_parameter (identifier) @block_param)

;; Metaprogramming with dynamic names (for honesty annotations)
(call method: (identifier) @meta_method
 (#match? @meta_method "^(define_method|class_eval|module_eval|instance_eval)$"))
```

**Error recovery:** Tree-sitter always returns a complete tree, even for syntactically broken files. Invalid regions produce `ERROR` nodes while the rest parses normally. The client skips `ERROR` nodes during extraction and logs a diagnostic. This aligns with the "errors never cascade" constraint — partial structure is better than no structure.

### Surface Model

The parser extracts a `RubyDocumentSurface` — a pure data model carrying everything needed for materialization. No tree-sitter types escape the parser.

```
RubyDocumentSurface
├── Modules[]          — name, qualified_name, nesting depth, span
│   ├── Methods[]      — name, visibility, is_static, params, accepts_block, span
│   ├── Constants[]    — name, span
│   └── Mixins[]       — module_name, mechanism (include/extend/prepend), ordinal
├── Classes[]          — name, qualified_name, superclass, has_superclass_declaration, span
│   ├── Methods[]      — name, visibility, is_static, params, accepts_block, span
│   ├── SingletonMethods[] — name, receiver, params, span
│   ├── Constants[]    — name, span
│   ├── Attributes[]   — name, accessor_type (reader/writer/accessor)
│   └── Mixins[]       — module_name, mechanism, ordinal
├── Functions[]        — top-level methods, name, params, accepts_block, span
├── Requires[]         — path, is_relative, span
├── Aliases[]          — new_name, original_name, span
├── MetaprogrammingHints[] — pattern_name, span, extractable (bool)
└── Stats              — class_count, module_count, method_count, line_count
```

**Key additions from review:** `has_superclass_declaration` on classes (for reopening detection), `accepts_block` on methods (from yield/block_parameter detection), `SingletonMethods` on classes, `ordinal` on mixins (for MRO), `MetaprogrammingHints` (for honesty annotations).

### Visibility Tracking

Ruby visibility is contextual — `private` applies to all subsequent method definitions until the next visibility modifier. The parser must track a visibility state machine per class/module scope:

```ruby
class User
  def public_method; end      # public (default)

  private                     # state changes to private

  def secret; end             # private
  def also_secret; end        # private

  protected                   # state changes to protected

  def for_subclasses; end     # protected

  public                      # state changes back to public

  def visible_again; end      # public
end
```

The tree-sitter query for visibility modifiers:

```scheme
;; Bare visibility modifier (scope change)
(call method: (identifier) @vis
 (#match? @vis "^(public|private|protected)$")
 !arguments)

;; Method-level visibility modifier
(call method: (identifier) @vis
      arguments: (argument_list (simple_symbol) @target)
 (#match? @vis "^(public|private|protected)$"))
```

When `private` / `protected` / `public` takes an argument (e.g., `private :method_name`), it applies to that specific method only. When bare, it changes the default for subsequent definitions. The parser tracks both forms.

### Metaprogramming (Recognizable Patterns)

The boundary: patterns with a syntactic footprint are extracted. Arbitrary `eval`, `define_method` with dynamic names, and `method_missing` are not — they would require execution. When unextractable metaprogramming is detected, an annotation is emitted so agents know the graph is incomplete.

| Pattern | Extracted as | Confidence |
|---------|-------------|------------|
| `attr_reader :name` | Method node (getter), marked `is_generated: true` | High |
| `attr_writer :name` | Method node (setter), marked `is_generated: true` | High |
| `attr_accessor :name` | Two method nodes (getter + setter), marked `is_generated: true` | High |
| `delegate :method, to: :target` | Method node, marked `is_generated: true`, `delegate_to` prop | High |
| `alias_method :new, :old` | Alias edge from new to old | High |
| `scope :name, -> { ... }` | Method node (class method), marked `is_generated: true` | Medium |
| `has_many :posts` | Edge (ASSOCIATES), props: `{association: "has_many", target: "posts"}` | Medium |
| `belongs_to :user` | Edge (ASSOCIATES), props: `{association: "belongs_to", target: "user"}` | Medium |
| `has_one :profile` | Edge (ASSOCIATES), props: `{association: "has_one", target: "profile"}` | Medium |
| `validates :email, ...` | Annotation (kind: `ruby.validation`, rule_id: field name) | Medium |
| `before_action :method` | Annotation (kind: `ruby.callback`, rule_id: callback type) | Medium |
| `after_action :method` | Annotation (kind: `ruby.callback`, rule_id: callback type) | Medium |
| `define_method(:name)` | Method node if name is a literal symbol/string | Low |
| `define_method(var)` | Annotation only: `ruby.metaprogramming` — "dynamic method definition detected, name not extractable" | — |
| `class_eval`, `module_eval` | Annotation only: `ruby.metaprogramming` — "eval-based definitions detected, content not extractable" | — |
| `method_missing` | Annotation only: `ruby.metaprogramming` — "method_missing defined, dynamic dispatch possible" | — |
| `eval(...)` | Not extracted, no annotation (too common as non-structural) | — |

"Medium" confidence means: the pattern is syntactically recognizable but its semantics depend on the framework (Rails, RSpec, etc.). The parser extracts what it sees; the framework interpretation is left to SQL views and agents.

**Honesty contract:** When the parser detects metaprogramming it cannot fully extract, it emits an annotation with `kind: ruby.metaprogramming` and a message describing what was detected and why it's incomplete. This fulfills the north-star requirement that agents can distinguish "what the graph captured" from "what it couldn't."

### Graph Materialization

State transfer via `RubyDocumentState` in `DocumentModel.Metadata`, following the PHP pattern.

**Nodes:**

| Kind | What | Key Props |
|------|------|-----------|
| `document` | Root node | `language`, `line_count`, `byte_size` |
| `rb.type` | Class or module | `name`, `qualified_name`, `kind` ("class"/"module"), `extends`, `namespace`, `accessibility`, `is_reopening` |
| `rb.member` | Method (instance, class, or singleton) | `name`, `qualified_name`, `kind` ("method"/"singleton_method"), `declaring_type`, `accessibility`, `is_static`, `parameters`, `return_type`, `accepts_block`, `is_generated`, `generator`, `receiver` |
| `rb.function` | Top-level method | `name`, `kind` ("function"), `parameters`, `accepts_block` |
| `rb.constant` | Constant assignment | `name`, `qualified_name` |
| `rb.property` | Attribute accessor declaration | `name`, `accessor_type` |

**Shared view participation:** The node kinds are chosen to match the shared cross-format views:
- `rb.type` matches `WHERE kind LIKE '%.type'` → appears in the shared `Types` view automatically
- `rb.member` and `rb.function` must be added to the shared `Functions` view's hardcoded kind list (`functions.sql`), and `'singleton_method'` added to the `$.kind` prop filter
- Standard property names (`name`, `qualified_name`, `kind`, `accessibility`, `extends`, `declaring_type`, `is_static`, `parameters`, `return_type`) match what the shared views project
- Class methods (`def self.foo`, `class << self`) use `kind: "method"` with `is_static: true` — same convention as `php.member`
- Singleton methods on specific objects use `kind: "singleton_method"` — Ruby-specific, appears in `ruby_methods` but not the shared `Functions` view

**Class vs module:** Distinguished by `props.kind`: `"class"` or `"module"`. One node kind, same as PHP uses one `php.type` for classes, interfaces, traits, and enums.

**Reopening detection:** A type node gets `is_reopening: true` when the class declaration has no superclass clause (`class Foo` without `< Bar`) AND another `rb.type` node with the same `qualified_name` exists with a superclass. This is a heuristic — it can be wrong when the original definition also has no superclass. The heuristic is conservative: if uncertain, `is_reopening` is `false`.

**Node kind naming:** Uses `rb.` prefix (dot-separated), following the code format convention (`csharp.type`, `php.type`). The `kind` property within props distinguishes subtypes.

**Edges:**

| Type | From | To | Props |
|------|------|----|-------|
| `HAS_PART` | document / class / module | child nodes | `ordinal` (source order) |
| `EXTENDS` | class | superclass | `target` (class name) |
| `INCLUDES` | class / module | included module | `target` (module name), `ordinal` |
| `PREPENDS` | class / module | prepended module | `target` (module name), `ordinal` |
| `EXTENDS_MODULE` | module (extend self) | self | — |
| `REQUIRES` | document | required path | `path`, `is_relative` |
| `ALIASES` | alias name | original name | `alias_type` (alias / alias_method) |
| `ASSOCIATES` | class | associated model | `association`, `target` |

**Mixin ordinals:** INCLUDES and PREPENDS edges carry `ordinal` tracking source order within the class/module. This enables method resolution order queries — Ruby's MRO follows reverse inclusion order (last included wins for include, first prepended wins for prepend).

**Reference edges** (EXTENDS, INCLUDES, PREPENDS, REQUIRES, ASSOCIATES): `IsComposition = false`, `DstId = null`, target name in `Props["target"]`. These are deferred references — resolved during multi-file analysis if both ends exist in the graph. This is the standard pattern across all RepoQL format loaders: C# has unresolved symbol keys, PHP has unresolved EXTENDS/IMPLEMENTS edges, TypeScript has unresolved import paths. Resolution happens in the multi-file analysis phase, not at parse time.

**Composition edges** (HAS_PART): `IsComposition = true`, `Ordinal` tracks source order. These form the containment tree: document → class/module → method/constant/property.

**Spans:** 1-based lines, 0-based bytes. Created via `DocumentModel.LineMap.GetSpan(startByte, endByte)`, same as PHP.

### X-Ray Summaries

**Headline:** Built in C# (no Liquid templates — following PHP convention).

```
{filename} | {primary_declaration} | {key_members} | {line_count} ln, ~{token_count} tok
```

Examples:

```
user.rb | class User < ApplicationRecord | authenticate, admin?, full_name | 180 ln, ~850 tok
concerns/searchable.rb | module Searchable | search, reindex | 45 ln, ~210 tok
routes.rb | 23 route definitions | 89 ln, ~420 tok
```

**Structure:** Indented outline with visibility symbols.

```
class User < ApplicationRecord
  include Authenticatable
  include Searchable
  has_many :posts
  has_many :comments
  validates :email
  +authenticate(password, &block)   #symbol=authenticate
  +admin?                           #symbol=admin?
  +full_name                        #symbol=full_name
  -validate_password_strength       #symbol=validate_password_strength
  ~name (attr_accessor)             #symbol=name
  ~email (attr_accessor)            #symbol=email
```

Visibility symbols: `+` public, `#` protected, `-` private, `~` generated (attr_accessor, delegate). The `#symbol=` anchors enable `read("file:///user.rb#symbol=authenticate")`. Block-accepting methods show `&block` in their parameter list.

### SQL Views

Embedded resource `Schema/ruby_views.sql`, registered via `IFormatSchemaProvider`.

```sql
-- ruby_types: classes and modules — the primary Ruby type view
-- Aggregates across files: a class reopened in 3 files appears once,
-- with definition_count and defined_in showing distribution.
-- Also participates in the shared Types view via rb.type kind.
CREATE OR REPLACE VIEW ruby_types AS
SELECT
    qualified_name,
    n.properties->>'kind' AS type_kind,
    MAX(n.properties->>'extends') AS extends,
    COUNT(*) AS definition_count,
    LIST(doc.uri ORDER BY COALESCE(n.properties->>'is_reopening', 'false')) AS defined_in,
    MIN(CASE WHEN COALESCE(n.properties->>'is_reopening', 'false') != 'true' THEN doc.uri END) AS origin_file,
    MAX(n.structure) AS structure
FROM node n
JOIN edge e ON e.destination_node_id = n.id
    AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE n.kind = 'rb.type'
GROUP BY n.properties->>'qualified_name', n.properties->>'kind';

-- ruby_methods: methods with their declaring type and parameter details
CREATE OR REPLACE VIEW ruby_methods AS
SELECT
    doc.uri AS document_uri,
    parent.uri AS type_uri,
    parent.properties->>'name' AS type_name,
    parent.properties->>'qualified_name' AS type_qualified_name,
    m.uri AS method_uri,
    m.headline,
    m.properties->>'name' AS name,
    m.properties->>'accessibility' AS visibility,
    COALESCE(m.properties->>'is_static', 'false') = 'true' AS is_class_method,
    COALESCE(m.properties->>'accepts_block', 'false') = 'true' AS accepts_block,
    COALESCE(m.properties->>'is_generated', 'false') = 'true' AS is_generated,
    m.properties->>'generator' AS generator,
    m.properties->>'parameters' AS parameters
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node parent ON parent.id = me.source_node_id
    AND parent.kind = 'rb.type'
JOIN edge de ON de.destination_node_id = parent.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE m.kind = 'rb.member';

-- ruby_mixins: include/extend/prepend relationships with ordinal for MRO
CREATE OR REPLACE VIEW ruby_mixins AS
SELECT
    src.uri AS type_uri,
    src.properties->>'name' AS type_name,
    src.properties->>'qualified_name' AS type_qualified_name,
    src.properties->>'kind' AS type_kind,
    e.type AS mechanism,
    e.properties->>'target' AS module_name,
    CAST(e.properties->>'ordinal' AS INTEGER) AS mixin_order
FROM edge e
JOIN node src ON src.id = e.source_node_id
WHERE e.type IN ('INCLUDES', 'PREPENDS', 'EXTENDS_MODULE')
  AND src.kind = 'rb.type'
ORDER BY src.id, mixin_order;

-- ruby_mro: method resolution order per class (within-file include/prepend order)
-- Ruby MRO: prepended modules first (in prepend order), then the class itself,
-- then included modules (in reverse include order), then superclass chain.
CREATE OR REPLACE VIEW ruby_mro AS
SELECT
    type_uri,
    type_name,
    type_qualified_name,
    module_name,
    mechanism,
    CASE mechanism
        WHEN 'PREPENDS' THEN 0
        WHEN 'INCLUDES' THEN 1
        WHEN 'EXTENDS_MODULE' THEN 2
    END AS mro_tier,
    mixin_order
FROM ruby_mixins
ORDER BY type_uri, mro_tier, mixin_order;

-- ruby_inheritance: class hierarchy
CREATE OR REPLACE VIEW ruby_inheritance AS
SELECT
    src.uri AS class_uri,
    src.properties->>'name' AS class_name,
    src.properties->>'qualified_name' AS qualified_name,
    e.properties->>'target' AS superclass_name
FROM edge e
JOIN node src ON src.id = e.source_node_id
WHERE e.type = 'EXTENDS' AND src.kind = 'rb.type';

-- ruby_constants: constant definitions with namespace
CREATE OR REPLACE VIEW ruby_constants AS
SELECT
    doc.uri AS document_uri,
    parent.properties->>'qualified_name' AS namespace,
    c.uri AS constant_uri,
    c.properties->>'name' AS name,
    c.properties->>'qualified_name' AS qualified_name
FROM node c
JOIN edge ce ON ce.destination_node_id = c.id
    AND ce.type = 'HAS_PART' AND ce.is_composition = TRUE
JOIN node parent ON parent.id = ce.source_node_id
JOIN edge de ON de.destination_node_id = parent.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE c.kind = 'rb.constant';

-- ruby_requires: dependency graph with internal/external classification
CREATE OR REPLACE VIEW ruby_requires AS
SELECT
    doc.uri AS document_uri,
    e.properties->>'path' AS required_path,
    COALESCE(e.properties->>'is_relative', 'false') = 'true' AS is_internal,
    CASE
        WHEN COALESCE(e.properties->>'is_relative', 'false') = 'true' THEN 'internal'
        ELSE 'external'
    END AS dependency_type
FROM edge e
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE e.type = 'REQUIRES';

-- ruby_aliases: method aliases
CREATE OR REPLACE VIEW ruby_aliases AS
SELECT
    src.uri AS source_uri,
    src.properties->>'name' AS alias_name,
    e.properties->>'alias_type' AS alias_type,
    dst.properties->>'name' AS original_name,
    dst.uri AS original_uri
FROM edge e
JOIN node src ON src.id = e.source_node_id
LEFT JOIN node dst ON dst.id = e.destination_node_id
WHERE e.type = 'ALIASES';

-- ruby_associations: Rails-style associations
CREATE OR REPLACE VIEW ruby_associations AS
SELECT
    src.uri AS model_uri,
    src.properties->>'name' AS model_name,
    src.properties->>'qualified_name' AS model_qualified_name,
    e.properties->>'association' AS association_type,
    e.properties->>'target' AS target_model
FROM edge e
JOIN node src ON src.id = e.source_node_id
WHERE e.type = 'ASSOCIATES' AND src.kind = 'rb.type';

-- ruby_validations: model validations from annotation table
CREATE OR REPLACE VIEW ruby_validations AS
SELECT
    doc.uri AS document_uri,
    a.rule_id AS field_name,
    a.message AS validation_rule,
    a.data->>'options' AS options
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id AND doc.kind = 'document'
WHERE a.kind = 'ruby.validation';

-- ruby_callbacks: controller/model callbacks from annotation table
CREATE OR REPLACE VIEW ruby_callbacks AS
SELECT
    doc.uri AS document_uri,
    a.rule_id AS callback_type,
    a.message AS callback_method,
    a.data->>'options' AS options
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id AND doc.kind = 'document'
WHERE a.kind = 'ruby.callback';

-- ruby_metaprogramming: honesty annotations about unextractable structure
CREATE OR REPLACE VIEW ruby_metaprogramming AS
SELECT
    doc.uri AS document_uri,
    a.message AS description,
    s.start_line AS line
FROM annotation a
JOIN node doc ON doc.id = a.scope_document_id AND doc.kind = 'document'
LEFT JOIN span s ON s.id = a.target_span_id
WHERE a.kind = 'ruby.metaprogramming';
```

### Error Handling

| Failure | Behavior |
|---------|----------|
| Tree-sitter parse produces ERROR nodes | Skip error regions, extract surrounding structure, emit diagnostic annotation |
| File can't be read (encoding, I/O) | `PipelineResult.Error` with diagnostic |
| X-ray summary build fails | Log warning, continue with null headline/structure |
| Visibility tracking ambiguous | Default to `public` — safe assumption for Ruby |
| Metaprogramming pattern unrecognized | Emit `ruby.metaprogramming` annotation with description of what was detected |
| Metaprogramming pattern recognized but not extractable | Emit `ruby.metaprogramming` annotation explaining what's missing from the graph |
| Tree-sitter native library missing | Startup failure with clear diagnostic pointing to NuGet package |

Each extraction phase (classes, methods, mixins, requires) is independently try/caught. A malformed class definition never prevents method extraction in another class.

---

## Cross-Cutting Concerns

**URI addressing:** Ruby files use `file:///path#symbol=ClassName.method_name` for symbol navigation. Class methods use `ClassName.method_name`, instance methods use `ClassName#method_name` (Ruby convention). The `#symbol=` fragment resolves through node name matching.

**Distributed definitions (open classes):** Each file that defines or reopens a class creates its own `rb.type` node with its own `HAS_PART` methods. This models reality — a class defined in 3 files produces 3 nodes. The `is_reopening` property distinguishes original definitions from reopenings. The `ruby_types` view aggregates by `qualified_name` to show the "one complete picture" the north-star requires — all methods, all mixins, all files, with the origin file identified.

**Method resolution order:** The `ruby_mro` view orders mixin edges by tier (prepends first, then includes) and ordinal (source order). This gives agents the within-file MRO. Full cross-file C3 linearization requires multi-file analysis to resolve mixin references to actual module definitions — same architectural pattern as C# cross-file symbol resolution.

**Deferred references:** Reference edges (EXTENDS, INCLUDES, PREPENDS, REQUIRES, ASSOCIATES) are created with `DstId = null` and target name in `Props["target"]`. This is the standard pattern across all RepoQL format loaders — C# has unresolved symbol keys, PHP has unresolved EXTENDS/IMPLEMENTS edges, TypeScript has unresolved import paths. Resolution happens in the multi-file analysis phase, not at parse time. Pre-resolution, queries like "what classes include Searchable?" match on `Props->>'target'` string comparison, which works for same-name matches.

**Search integration:** `Artifact.Text` contains the source code and participates in semantic search. Node headlines and structure text make classes and methods discoverable via explore.

**Metaprogramming honesty:** When the parser encounters `define_method` with a variable, `class_eval`, `module_eval`, or `method_missing`, it emits a `ruby.metaprogramming` annotation. The `ruby_metaprogramming` view surfaces these. Agents can query it to understand what the graph might be missing for a given file.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| TreeSitter.DotNet | Prism P/Invoke | Self-contained NuGet (no native build pipeline), Ruby grammar bundled, all platforms covered. Single-maintainer risk accepted — grammar is the official tree-sitter-ruby |
| TreeSitter.DotNet | Prism subprocess | No Ruby runtime dependency. Eliminates deployment constraint |
| TreeSitter.DotNet | ANTLR4 | No production-quality ANTLR Ruby grammar exists. Tree-sitter-ruby has 1,025 commits and is used by GitHub |
| TreeSitter.DotNet | Heuristic/regex | Tree-sitter handles nested structures, heredocs, string interpolation correctly. Heuristic would need a state machine for ~95% coverage; tree-sitter gives ~98% with less code |
| Query-based extraction | Full CST traversal | Queries are declarative, structural, and more robust to grammar evolution. PHP's visitor pattern is effective but verbose |
| Separate nodes per file (open classes) | Merged class entity | Graph models reality — a class defined in 3 files produces 3 `rb.type` nodes. `ruby_types` view aggregates. No lossy merging, no single-writer conflicts |
| `rb.member` for all methods | Separate `rb.instance_method` / `rb.class_method` node kinds | Matches shared Functions view convention (`php.member`, `csharp.member`). `is_static` prop and `kind` prop distinguish method types. One node kind, queried with filters |
| `kind: "singleton_method"` on `rb.member` | Folding into `kind: "method"` | Singleton methods on specific objects (not `self`) have a receiver that regular methods don't. Rare, Ruby-specific — separate `kind` value keeps the common `method` path clean for shared views |
| Framework patterns in v1 | Deferring to extension | Rails associations, validations, and callbacks are the questions agents actually ask. A Ruby indexer that ignores Rails misses the highest-value queries |
| Metaprogramming annotations for unextractable patterns | Silent skip | North-star requires agents know what the graph couldn't capture. Silence violates the honesty contract |
| Medium-confidence metaprogramming | Only extracting `def` | `attr_accessor` creates real methods. Hiding them hides the API surface. Marking confidence lets agents decide how much to trust |

## Alternatives Considered

**Prism via P/Invoke:** 100% Ruby coverage, C99 with zero deps, maintained by Ruby core team. Rejected for v1: requires compiling `libprism` native binaries for all six platforms, writing a binary format deserializer, and maintaining a custom NuGet packaging pipeline. Higher ceiling but much higher implementation cost. Could replace TreeSitter.DotNet in the future if coverage gaps matter.

**Prism via subprocess:** Same pattern as TypeScript loader. Rejected: adds Ruby runtime as a deployment dependency. RepoQL indexes repositories — it shouldn't require the languages it indexes to be installed.

**ANTLR4:** Natural choice given PHP precedent. Rejected: no production-quality Ruby grammar exists. Ruby's `/` ambiguity (division vs regex) and optional parentheses make ANTLR grammar authoring a research-grade problem.

**Heuristic parsing:** Proven by universal-ctags, zero dependencies. Rejected for v1: tree-sitter gives better accuracy with less code. The heuristic approach could be a fallback if tree-sitter native libraries cause deployment issues.

## Risks

| Risk | Mitigation |
|------|------------|
| TreeSitter.DotNet single maintainer (10 GitHub stars) | Grammar source is official tree-sitter-ruby (219 stars, 40+ contributors). If the NuGet wrapper is abandoned, the grammar and native libraries can be packaged independently |
| TreeSitter.DotNet P/Invoke overhead unknown | No benchmarks for .NET binding overhead exist (research gap). Hands-on testing during implementation required |
| TreeSitter.DotNet reliability unvalidated | Limited community usage (14K downloads). Integration tests against diverse Ruby files needed to surface edge cases |
| Native library loading fails on some platform | Test all six RIDs in CI. Fallback: heuristic parser for platforms where tree-sitter fails |
| Tree-sitter-ruby grammar lags behind Ruby trunk | Grammar covers through Ruby 3.1 patterns. New syntax additions are typically minor and additive. Monitor tree-sitter-ruby releases |
| Thread-safety bugs in `ThreadLocal<Parser>` | Unit test concurrent parsing. Tree-sitter documentation is clear on the constraint. Research notes this as untested in practice |
| Metaprogramming detection too aggressive (false positives) | Conservative: only extract patterns listed in the design. Mark confidence level. Agents can filter by confidence |
| Metaprogramming detection too conservative (false negatives) | Extension point: new patterns can be added to queries without changing architecture. `attr_accessor`, `has_many`, `validates` cover the highest-value 80% |
| Open class aggregation confuses agents | `ruby_types` view aggregates by qualified_name, showing definition_count and origin_file. Shared `Types` view shows per-node locality. Both perspectives available |
| `is_reopening` heuristic wrong | Conservative: defaults to `false` when uncertain. Agents can use `ruby_types.definition_count > 1` as a more reliable signal |

## Extension Points

- **Prism backend:** Replace tree-sitter with Prism P/Invoke if coverage gaps are found. Surface model and materialization unchanged — only the parser changes
- **Full MRO computation:** Multi-file analysis resolves INCLUDES/PREPENDS to actual module node IDs, enabling recursive C3 linearization via UDF
- **ERB template support:** Separate loader for `.erb` files that extracts embedded Ruby fragments and sends them through the Ruby parser (composition pattern from formats north-star)
- **Gemspec/Gemfile dependency graph:** Extract gem dependencies as REQUIRES edges with version constraints in props
- **RBS type annotation support:** If `.rbs` type signature files are present, link type information to method/class nodes
- **Circular/missing require detection:** Multi-file analysis emits annotations for cycles in the REQUIRES graph and unresolved paths
- **Framework-specific views:** `rails_models`, `rails_controllers`, `rspec_examples` — additional convenience views on top of the base views

---

## Project Structure

```
src/Formats/RepoQL.Formats.Ruby/
    RubyLoader.cs                          # IFormatLoader + IFormatMaterializer + IFormatSchemaProvider
    RubyClassifier.cs                      # IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
    RubyParser.cs                          # IAsyncPipeline<IClassifiedArtifact, Records?>
    RubyDocumentState.cs                   # State transfer between Load and Materialize
    RubyConstants.cs                       # Node kinds, edge types, property keys, media types
    Surface/
        RubyDocumentSurface.cs             # Root surface model
        RubyClassInfo.cs                   # Class data (with has_superclass_declaration)
        RubyModuleInfo.cs                  # Module data
        RubyMethodInfo.cs                  # Method data (with accepts_block)
        RubySingletonMethodInfo.cs         # Singleton method data (with receiver)
        RubyMixinInfo.cs                   # Include/extend/prepend data (with ordinal)
        RubyRequireInfo.cs                 # Require/require_relative data
        RubyMetaprogrammingHint.cs         # Detected but unextractable patterns
    TreeSitter/
        RubyTreeSitterClient.cs            # Tree-sitter wrapper (contains all native interop)
        RubyQueries.cs                     # S-expression query strings
    Schema/
        ruby_views.sql
    RubyServiceCollectionExtensions.cs
    RepoQL.Formats.Ruby.csproj             # References: TreeSitter.DotNet, RepoQL.Contracts, RepoQL.Indexing

src/tests/RepoQL.Formats.Ruby.Tests/
    RubyLoaderTests.cs                     # Load + Materialize round-trip
    RubyTreeSitterClientTests.cs           # Parser extraction correctness
    RubyVisibilityTests.cs                 # Visibility state machine
    RubyMetaprogrammingTests.cs            # attr_accessor, delegate, scope, honesty annotations
    RubyMixinTests.cs                      # include/extend/prepend edge creation and MRO order
    RubySingletonMethodTests.cs            # Singleton method and singleton class extraction
    RubyOpenClassTests.cs                  # Reopening detection and ruby_types aggregation
    RubyConcurrentParsingTests.cs          # Thread-safety of ThreadLocal<Parser>
    Fixtures/
        simple_class.rb
        module_with_methods.rb
        open_class_part1.rb
        open_class_part2.rb
        visibility_modifiers.rb
        metaprogramming.rb
        unextractable_metaprogramming.rb
        rails_model.rb
        singleton_methods.rb
        block_accepting_methods.rb
        constants_and_namespaces.rb
        require_dependencies.rb
        malformed.rb
    RepoQL.Formats.Ruby.Tests.csproj       # References: TUnit, AwesomeAssertions, FakeItEasy
```

---

*Parse the tree. Build the graph. Be honest about what's missing. Let SQL do the rest.*
