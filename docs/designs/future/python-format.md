---
description: Design for Python format support — extracting classes, functions, imports, decorators, type annotations, and relationships from Python source via tree-sitter
tags: [format, python, tree-sitter, design, code]
audience: { human: 45, agent: 55 }
purpose: { design: 85, flow: 15 }
---

# Python Format — Design

## North Star

An agent should understand a Python codebase's classes, functions, imports, type system, and package structure — without reading source files — and query it all through the same SQL surface as every other format. When a decorator changes what something is, the agent sees the semantic effect. When a type hint declares a contract, the agent queries it as structured data. Where dynamism makes structure invisible, the graph says so honestly.

**Informed by:** `docs/north-star/formats/python.md`
**Research:** `docs/research/python-parsing.md`

## Context

Python files appear in repositories as application code, library modules, tests, configuration, migrations, CLI tools, type stubs, and framework hosts (Django views, Flask routes, FastAPI endpoints). Python's syntax is indentation-sensitive, its type system is optional and gradual, and its dynamism ranges from the fully static (dataclasses, enums) to the fully opaque (`exec`, metaclasses, monkey patching).

The Ruby format loader established the pattern for using tree-sitter in a code format. This design follows that pattern closely: `TreeSitterClient` wraps all native interop, S-expression queries extract structure, a surface model carries data to materialization, and SQL views expose Python-specific queries. The loader/parser/materializer split, state transfer via `DocumentModel.Metadata`, and SQL view registration are identical.

**Key differences from Ruby:**
- Python has convention-based visibility (`_private`, `__mangled`) instead of Ruby's keyword-based visibility state machine
- Python has decorators that change what something *is* (`@property`, `@staticmethod`, `@dataclass`) — Ruby's metaprogramming does similar things but through method calls, not decorator syntax
- Python has a gradual type system with annotations on parameters, returns, and variables — Ruby has no native type annotation syntax
- Python has multiple inheritance with C3 linearization — Ruby has single inheritance with mixins
- Python has `__init__.py` for package structure and namespace packages (implicit, no `__init__.py`) — Ruby uses directory conventions
- Python has `.pyi` type stub files — no Ruby equivalent

## Constraints

| Constraint | Source |
|------------|--------|
| Single writer to DuckDB | Hard constraint — all writes through `DuckDbDataStore` |
| Frozen schema — 5 tables only | Extend via views/macros/UDFs, never new tables |
| Errors never cascade | One malformed Python file must never stop indexing |
| TreeSitter.DotNet (MIT) | NuGet package with Python grammar bundled. Cross-platform native binaries |
| No Python runtime | Parser runs in-process via native tree-sitter library. No `python` on PATH required |
| tree-sitter parsers not thread-safe | Each thread needs its own Parser instance |

---

## Design

### Classification

Python files get provisional media type `text/x-python` from the naming convention layer. The classifier confirms and adds the kind parameter.

| Extension / Name | Kind | Notes |
|------------------|------|-------|
| `.py` | `code.python` | Standard Python source |
| `.pyw` | `code.python` | Windows GUI Python — same grammar |
| `.pyi` | `code.python.stub` | Type stub files — parsed identically, marked for linking |
| `__init__.py` | `code.python` | Package init — detected from filename during materialization |
| `__main__.py` | `code.python` | Entry point — detected from filename during materialization |
| `Pipfile` | Not handled | TOML format — separate loader |
| `setup.py` | `code.python` | Legacy setup script — parsed as standard Python |
| `conftest.py` | `code.python` | pytest conftest — parsed as standard Python |

```csharp
SemanticMediaType.Create("text", "x-python").WithKind("code.python")
SemanticMediaType.Create("text", "x-python").WithKind("code.python.stub")
```

Package init and entry point roles are not separate kinds — they're standard Python source. The classifier doesn't need to distinguish them; materialization checks the filename to set role annotations.

### Tree-Sitter Integration

All tree-sitter interaction is contained behind `PythonTreeSitterClient` — no tree-sitter types escape this class. Same architectural pattern as `RubyTreeSitterClient`.

```
PythonTreeSitterClient
├── Parse(string sourceCode) → PythonDocumentSurface
├── Thread-local Parser instances (tree-sitter is not thread-safe)
└── S-expression queries for symbol extraction
```

**Thread safety:** `ThreadLocal<Parser>` per the Ruby pattern. The `Language` object is created once and shared — immutable and thread-safe.

```csharp
private static readonly Language SharedLanguage =
    new Language("tree-sitter-python", "tree_sitter_python");

private readonly ThreadLocal<Parser> _parsers =
    new(() => new Parser(SharedLanguage), trackAllValues: true);
```

**Indentation:** Handled natively by tree-sitter-python's external scanner (C code). INDENT/DEDENT tokens are generated correctly without any .NET-side work. This is a decisive advantage over ANTLR4, which requires a custom `PythonLexerBase.cs` for indentation.

**Query-based extraction:** S-expression queries target specific patterns. Key queries:

```scheme
;; Classes with base classes
(class_definition
  name: (identifier) @name
  superclasses: (argument_list)? @bases
  body: (block) @body)

;; Functions with parameters and return types
(function_definition
  name: (identifier) @name
  parameters: (parameters) @params
  return_type: (type)? @return_type
  body: (block) @body)

;; Decorated definitions (captures decorator + definition together)
(decorated_definition
  (decorator) @decorator
  definition: (_) @definition)

;; Import statements
(import_statement
  name: (dotted_name) @module)

;; From-imports with import list
(import_from_statement
  module_name: (dotted_name)? @module
  name: (import_list
    (dotted_name) @imported_name))

;; From-imports with aliases
(import_from_statement
  module_name: (dotted_name)? @module
  name: (import_list
    (aliased_import
      name: (dotted_name) @imported_name
      alias: (identifier) @alias)))

;; Star imports
(import_from_statement
  module_name: (dotted_name)? @module
  (wildcard_import) @star)

;; Module-level assignments (constants, __all__, __slots__)
(module
  (expression_statement
    (assignment
      left: (identifier) @name
      right: (_) @value)))

;; Self-attribute assignments in method bodies (instance variables)
(assignment
  left: (attribute
    object: (identifier) @self
    attribute: (identifier) @attr_name)
  (#eq? @self "self"))

;; Type alias statements (3.12+)
(type_alias_statement
  name: (identifier) @alias_name
  value: (type) @alias_value)

;; Yield expressions (generator detection)
(yield) @yield_site

;; Async with statements (async context manager usage)
(with_statement "async") @async_with_site

;; Async for statements (async iteration usage)
(for_statement "async") @async_for_site

;; Metaprogramming patterns
(call
  function: (identifier) @func_name
  (#match? @func_name "^(exec|eval|type|__import__|setattr)$"))
```

**Note on query accuracy:** These S-expression patterns reflect the tree-sitter-python grammar's documented structure. Exact field names and node types must be verified against the grammar during implementation. The Ruby format's query set was refined iteratively through testing — the Python format should follow the same approach.

**Error recovery:** Tree-sitter produces partial parse trees for malformed files. ERROR nodes sit alongside valid structure. The client skips ERROR nodes during extraction, logs a diagnostic, and returns whatever structure was recoverable.

### Surface Model

The parser extracts a `PythonDocumentSurface` — a pure data model carrying everything needed for materialization. No tree-sitter types escape the parser.

```
PythonDocumentSurface
├── Classes[]
│   ├── Name, QualifiedName, BaseClasses[], Metaclass
│   ├── Decorators[] (name, arguments text)
│   ├── Methods[]
│   │   ├── Name, IsAsync, IsGenerator, IsAsyncGenerator
│   │   ├── UsesAsyncWith, UsesAsyncFor
│   │   ├── Decorators[] (name, arguments text)
│   │   ├── Parameters[] (name, type, default, kind: positional/keyword/var_positional/var_keyword)
│   │   ├── ReturnType
│   │   └── ByteRange
│   ├── ClassVariables[] (name, type_annotation, value_text)
│   ├── InstanceVariables[] (name, type_annotation) — from __init__
│   ├── Slots — __slots__ list if present
│   ├── Docstring
│   └── ByteRange
├── Functions[] — top-level
│   ├── Name, IsAsync, IsGenerator, IsAsyncGenerator
│   ├── UsesAsyncWith, UsesAsyncFor
│   ├── Decorators[], Parameters[], ReturnType
│   ├── Docstring
│   └── ByteRange
├── Imports[]
│   ├── Module, Names[] (name, alias), IsRelative, RelativeLevel, IsStar
│   ├── IsTypeCheckingOnly
│   └── ByteRange
├── Constants[] — module-level
│   ├── Name, TypeAnnotation, ValueText, IsFinal, IsAllCaps
│   └── ByteRange
├── TypeAliases[] — type X = ... (3.12+) and X: TypeAlias = ...
│   ├── Name, Definition
│   └── ByteRange
├── AllExports — __all__ list (string[]) if present
├── ModuleDocstring
├── MetaprogrammingHints[] (pattern_name, byte_range, extractable)
├── FrameworkHints[] (kind, rule_id, message, byte_range)
└── Stats (class_count, function_count, import_count, line_count, error_node_count)
```

**Key design choices:**
- **Decorators** are `(string Name, string? Arguments)` records. `Name` is the full dotted name (`pytest.fixture`, `app.route`). `Arguments` is the raw argument text if the decorator is called.
- **Parameters** carry their kind (`positional_only`, `positional_or_keyword`, `keyword_only`, `var_positional`, `var_keyword`) because Python's parameter model is richer than Ruby's.
- **InstanceVariables** are extracted from `__init__` only. `self.x` assignments in other methods are intentionally excluded — they're too unreliable as API surface.
- **ClassVariables** are class-level assignments with type annotations or assignments to recognized patterns. Captured alongside instance variables to give a complete picture of a class's state.
- **Metaclass** is extracted from class keyword arguments (`class Foo(metaclass=ABCMeta)`). Stored as a string property on the class info.
- **IsTypeCheckingOnly** on imports detects the `if TYPE_CHECKING:` guard pattern by checking if the import statement's parent is an `if_statement` whose condition references `TYPE_CHECKING`.
- **TypeAliases** are separate from constants because they represent type-level declarations, not values.
- **UsesAsyncWith / UsesAsyncFor** are detected by scanning function bodies for `async with` and `async for` statements. These are structural annotations on async functions, per the north-star.
- **FrameworkHints** detect recognizable framework patterns (ORM fields, fixtures) as annotations, following the Ruby convention for Rails patterns.

### Visibility

Python's visibility is convention-based — no keywords, no state machine:

| Convention | Meaning | Graph value |
|------------|---------|-------------|
| `name` | Public | `public` |
| `_name` | Private by convention | `private` |
| `__name` | Name-mangled (class-private) | `private` |
| `__name__` | Dunder (protocol method) | `public` |

Resolution is purely syntactic — inspect the method/variable name. No state tracking needed (unlike Ruby's visibility state machine).

Dunder methods (`__init__`, `__str__`, `__enter__`) are always public regardless of the leading underscores. Detected by the `__name__` pattern (leading AND trailing double underscores).

### Decorator Semantics

Some decorators change what something *is*. The materializer recognizes built-in decorators and maps them to node properties:

| Decorator | Effect on node |
|-----------|---------------|
| `@property` | `kind: "property"` on `py.member` |
| `@staticmethod` | `is_static: true` on `py.member` (kind stays `"method"`) |
| `@classmethod` | `is_static: true`, `is_classmethod: true` on `py.member` (kind stays `"method"`) |
| `@abstractmethod` | `is_abstract: true` on `py.member` |
| `@dataclass` | `type_kind: "dataclass"` on `py.type` |
| `@overload` | `is_overload: true` on `py.member` — signals signature variant |
| `@override` | Annotation only — informational |

**Why `classmethod`/`staticmethod` keep `kind: "method"`:** The shared `Functions` view filters on `kind IN ('method', 'constructor', 'function')`. Using `kind: "classmethod"` or `kind: "staticmethod"` would silently exclude these from cross-format queries. Instead, `is_static` and `is_classmethod` properties distinguish them. This matches the Ruby convention where class methods use `kind: "method"` with `is_static: true`.

The full decorator list is stored in `props.decorators` as a JSON array (e.g., `["property", "cache"]`). The semantic mapping is applied *in addition* — the decorator is both recorded as text and interpreted for its structural effect.

**Class kind inference:** The materializer determines `type_kind` from decorators and base classes:

| Condition | `type_kind` |
|-----------|-------------|
| `@dataclass` decorator | `dataclass` |
| Inherits from `Enum`, `IntEnum`, `Flag`, `IntFlag`, `StrEnum` | `enum` |
| Inherits from `NamedTuple` | `namedtuple` |
| Inherits from `TypedDict` | `typeddict` |
| Inherits from `Protocol` | `protocol` |
| Inherits from `ABC` or uses `ABCMeta` metaclass | `abstract` |
| None of the above | `class` |

Detection uses the last component of the base class name — `models.Model` matches `Model`, not `models`. This is a heuristic. Fully qualified resolution would require cross-file analysis.

**Protocol classes:** A class with `type_kind: "protocol"` has its methods appear normally in `python_methods`. Agents can query `SELECT * FROM python_methods WHERE type_qualified_name IN (SELECT qualified_name FROM python_types WHERE type_kind = 'protocol')` to list a Protocol's required methods and signatures, satisfying the north-star declaration.

### Type Annotation Extraction

Type annotations are extracted as text and stored in node properties. No type resolution or inference — the graph records what was written.

| Location | Storage |
|----------|---------|
| Parameter type | `parameters` JSON: `[{"name": "x", "type": "int", ...}]` |
| Return type | `return_type` property on function/method node |
| Variable annotation | `type` field in variable/constant JSON entry |
| Class variable annotation | `type` field in class variable JSON entry |
| Instance variable from `__init__` | `type` property if `self.x: Type = ...` form used |

Generic type parameters (both `TypeVar` style and 3.12+ `[T]` syntax) are captured as part of the class/function signature text. The graph doesn't model type parameters as separate entities — that's type-checker territory.

**Why no HAS_TYPE edges:** Type annotations are properties of the entities they annotate (a parameter's type, a function's return type, a variable's type), not independent relationships. Storing them as properties keeps queries simple — `python_methods WHERE return_type LIKE '%User%'` — and avoids an edge explosion (every typed parameter would generate an edge). This matches the Ruby pattern where similar properties (parameters, return type) are stored as text on nodes.

### Instance Variable Discovery

Instance variables are discovered from `__init__` method bodies only:

```python
class User:
    def __init__(self, name: str, email: str):
        self.name = name          # → instance var: name (type: str, from parameter)
        self.email = email        # → instance var: email (type: str, from parameter)
        self._cache: dict = {}    # → instance var: _cache (type: dict, from annotation)
        self.active = True        # → instance var: active (type: None — no annotation)
```

**Extraction rules:**
1. Find the `__init__` method in each class
2. Walk its body for `self.x = ...` assignments
3. If the target has an inline type annotation (`self.x: Type = ...`), use it
4. If the target name matches a typed parameter, inherit the parameter's type
5. If neither, record with no type

Instance variables from methods other than `__init__` are excluded. They're too unreliable — a `self.temp = ...` in a helper method is not part of the class's API surface. This is a deliberate boundary, not a gap.

### Docstring Extraction

Docstrings are the first expression statement in a module, class, or function body, if it's a string literal.

```python
"""Module docstring."""  # → module docstring

class User:
    """User model."""    # → class docstring

    def greet(self):
        """Return greeting."""  # → method docstring
        return f"Hello, {self.name}"
```

**v1 scope:** Docstrings are stored as raw text in a `docstring` property on the node. Raw text participates in semantic search, so docstring content is discoverable via explore. Structured parsing of Google/NumPy/Sphinx docstring formats — extracting parameter descriptions, return descriptions, and raised exceptions into annotations — is an extension point.

### Import Handling

Imports are materialized as `IMPORTS` edges on the document node.

| Python syntax | Edge properties |
|---------------|----------------|
| `import os` | `specifier: "os"`, `names: null`, `is_relative: false` |
| `import os.path` | `specifier: "os.path"`, `names: null`, `is_relative: false` |
| `from os import path, getcwd` | `specifier: "os"`, `names: "path,getcwd"`, `is_relative: false` |
| `from os import path as p` | `specifier: "os"`, `names: "path:p"`, `is_relative: false` |
| `from os import *` | `specifier: "os"`, `names: "*"`, `is_relative: false` |
| `from . import utils` | `specifier: "."`, `names: "utils"`, `is_relative: true`, `relative_level: 1` |
| `from ..core import Base` | `specifier: "..core"`, `names: "Base"`, `is_relative: true`, `relative_level: 2` |

**Aliased imports** use `name:alias` format in the names list (e.g., `"path:p"`) so agents can trace both the original name and its local alias.

**TYPE_CHECKING guard detection:** The parser checks if an import statement is inside an `if TYPE_CHECKING:` block. If so, the edge gets `is_type_checking_only: true`. This is syntactic detection — look for an `if_statement` ancestor whose condition is `TYPE_CHECKING` or `typing.TYPE_CHECKING`.

**Import classification (internal vs. external):** Relative imports are definitively internal. Absolute imports require project-structure context — classification happens in multi-file analysis, not at parse time. The `python_imports` view marks relative imports as `internal` and all others as `unknown`. Multi-file analysis can reclassify `unknown` to `internal` or `external` based on what exists in the graph. Unresolved imports (specifiers that match nothing in the project) are detectable via multi-file analysis and emitted as annotations — same pattern as the unresolved-reference design (`docs/designs/current/unresolved-references.md`).

### Framework Patterns

Python's highest-value framework patterns have syntactic footprints. Following the Ruby design's convention — which includes Rails `has_many`, `validates`, and `before_action` in v1 — the Python format extracts the most common framework patterns at medium confidence.

**Decorator-based patterns** are already captured by the decorator system. `@pytest.fixture`, `@app.route("/path")`, `@router.get("/users")`, `@click.command()` all appear in the `decorators` JSON array on their target node. Agents query these via `python_methods WHERE decorators LIKE '%pytest.fixture%'`. No additional extraction needed.

**ORM field patterns** require explicit detection — they're class-level assignments that happen to be method calls:

| Pattern | Extracted as | Confidence |
|---------|-------------|------------|
| `name = models.CharField(max_length=100)` | Annotation: `python.framework`, `rule_id: "django_field"`, `message: "CharField(max_length=100)"` | Medium |
| `name = db.Column(db.String)` | Annotation: `python.framework`, `rule_id: "sqlalchemy_column"`, `message: "Column(String)"` | Medium |
| `name = Field(default=...)` | Annotation: `python.framework`, `rule_id: "pydantic_field"`, `message: "Field(default=...)"` | Medium |

Detection: class-level assignments where the value is a call to a dotted name matching known patterns (`models.*`, `db.Column`, `Field`). The message captures the call expression text for queryability.

**Decorator-based framework patterns** (`@app.before_request`, `@receiver(post_save)`, `@pytest.fixture`) are NOT double-captured as annotations. They're already in the `decorators` JSON array on the node. Querying `python_methods WHERE decorators LIKE '%before_request%'` works. No redundant annotation needed.

### Metaprogramming (Honest Boundaries)

Same contract as Ruby. Patterns with a syntactic footprint are extracted. Unextractable dynamism gets an annotation so agents know the graph is incomplete.

| Pattern | Extracted as | Confidence |
|---------|-------------|------------|
| `@dataclass` fields | Class variables in `variables` JSON, generated `__init__` as method with `is_generated: true` | High |
| `@property` | Method node with `kind: "property"` | High |
| `NamedTuple` fields | Entries in `variables` JSON on `py.type` | High |
| `Enum` members | Entries in `constants` JSON on document | High |
| `__slots__` | Property on `py.type` node (`slots`) | High |
| `__all__` | Property on `document` node (`all_exports`) | High |
| `type X = ...` (3.12+) | Entry in `type_aliases` JSON on document | High |
| `X: TypeAlias = ...` | Entry in `type_aliases` JSON on document | Medium |
| `__getattr__` defined on class | Annotation: `python.metaprogramming` — "dynamic attribute access, graph may be incomplete" | — |
| `exec(...)` / `eval(...)` | Annotation: `python.metaprogramming` — "dynamic code execution detected" | — |
| `type()` call (3-arg form) | Annotation: `python.metaprogramming` — "dynamic class creation" | — |
| `setattr(...)` | Annotation: `python.metaprogramming` — "dynamic attribute creation" | — |
| Metaclass with `__new__` or `__init_subclass__` | Annotation: `python.metaprogramming` — "metaclass may generate members" | — |
| Monkey patching | Not reliably detectable from single-file syntax — `SomeClass.attr = value` is syntactically identical to legitimate class attribute modification. Annotation emitted only when a module-level attribute assignment targets a `type()` call or uses a function/lambda value: `python.metaprogramming` — "possible monkey patch" | — |

**Dataclass field generation:** When `@dataclass` is detected, the class's annotated class variables are treated as fields. The materializer generates a `__init__` method node marked `is_generated: true`, `generator: "dataclass"`. Only `__init__` is generated in v1 — `__repr__`, `__eq__`, `__hash__` generation depends on decorator arguments (`@dataclass(eq=False)`) which requires parsing decorator arguments. Since decorator argument parsing is an extension point, only the unconditional `__init__` is generated (unless the class has an explicit `__init__`, in which case no generated one is emitted).

**Honesty contract:** When the parser detects metaprogramming it cannot fully extract, it emits an annotation with `kind: python.metaprogramming` and a message describing what was detected and why it's incomplete.

### Graph Materialization

State transfer via `PythonDocumentState` in `DocumentModel.Metadata`, following the Ruby pattern.

**Nodes:**

| Kind | What | Key Props |
|------|------|-----------|
| `document` | Root node | `language: "python"`, `line_count`, `byte_size`, `role` (module/package_init/entry_point/stub), `docstring`, `all_exports`, `constants` (JSON), `type_aliases` (JSON) |
| `py.type` | Class | `name`, `qualified_name`, `type_kind` (class/dataclass/enum/namedtuple/typeddict/protocol/abstract), `extends` (comma-separated base classes), `metaclass`, `namespace`, `decorators` (JSON array), `is_abstract`, `docstring`, `slots`, `variables` (JSON — instance and class variables with name, type, variable_kind) |
| `py.member` | Method / property | `name`, `qualified_name`, `kind` (method/property), `declaring_type`, `accessibility` (public/private), `is_static`, `is_classmethod`, `is_async`, `is_generator`, `uses_async_with`, `uses_async_for`, `parameters` (JSON), `return_type`, `decorators` (JSON array), `is_generated`, `generator`, `is_overload`, `docstring` |
| `py.function` | Top-level function | `name`, `kind: "function"`, `is_async`, `is_generator`, `uses_async_with`, `uses_async_for`, `parameters` (JSON), `return_type`, `decorators` (JSON array), `docstring` |

Four node kinds. Constants, variables, and type aliases are attributes — not independently addressable entities.

- **Constants** — JSON array on the document node: `[{"name": "MAX_RETRIES", "type": "int", "is_final": true, "value_preview": "3"}]`. Visible in structure text and headlines. Search finds them through structure and artifact text.
- **Variables** (instance and class) — JSON array on the `py.type` node: `[{"name": "db", "type": "Database", "variable_kind": "instance"}, {"name": "MAX_SIZE", "type": "int", "variable_kind": "class"}]`. Visible in structure text (`~db: Database`).
- **Type aliases** — JSON array on the document node: `[{"name": "UserId", "definition": "int | str"}]`. Visible in structure text.

**Shared view participation:**
- `py.type` matches `WHERE kind LIKE '%.type'` — appears in the shared `Types` view
- `py.member` and `py.function` need addition to the shared `Functions` view's kind list. Both use `kind: "method"` or `kind: "function"` which already match the existing filter. `kind: "property"` is Python-specific and intentionally excluded from the shared view (properties have different semantics than callable functions)
- Standard property names (`name`, `qualified_name`, `kind`, `accessibility`, `extends`, `declaring_type`, `is_static`, `parameters`, `return_type`) match shared view projections

**Node kind naming:** Uses `py.` prefix (dot-separated), following the code format convention (`rb.type`, `csharp.type`, `php.type`).

**Edges:**

| Type | From | To | Props |
|------|------|----|-------|
| `HAS_PART` | document / class | child nodes | `ordinal` (source order) |
| `EXTENDS` | class | base class | `target` (class name), `ordinal` (for MRO — first base = 0) |
| `IMPORTS` | document | imported module | `specifier`, `names`, `is_relative`, `relative_level`, `is_type_checking_only` |

**Multiple inheritance ordinals:** Python's EXTENDS edges carry `ordinal` tracking the position in the base class list. `class Foo(A, B, C)` produces three EXTENDS edges with ordinals 0, 1, 2. This enables MRO queries — Python uses C3 linearization, which depends on base class order.

**Reference edges** (EXTENDS, IMPORTS): `IsComposition = false`, `DstId = null`, target name in props. Deferred references resolved during multi-file analysis. Same standard pattern as Ruby, C#, PHP, TypeScript.

**Composition edges** (HAS_PART): `IsComposition = true`, `Ordinal` tracks source order.

**Why no DECORATED_BY edges:** Decorators are properties on the decorated node, not separate graph entities. This keeps the graph simple — `python_methods WHERE decorators LIKE '%route%'` is more natural than joining through decorator edges. The `decorators` JSON array can be projected into a `python_decorators` view if cross-codebase decorator queries become important.

**Spans:** 1-based lines, 0-based bytes. Created via `DocumentModel.LineMap.GetSpan(startByte, endByte)`.

### X-Ray Summaries

**Headline:** Built in C# (no Liquid templates — following Ruby convention).

```
{filename} | {primary_declaration} | {key_members} | {line_count} ln, ~{token_count} tok
```

Examples:

```
views.py | class UserViewSet(ModelViewSet) | list, create, retrieve, update | 280 ln, ~1.4k tok
models.py | 3 classes | User, Profile, Session | 420 ln, ~2.1k tok
utils.py | 8 functions | validate_token, hash_password, parse_jwt, ... | 150 ln, ~0.7k tok
constants.py | 5 constants | MAX_RETRIES, TIMEOUT, DEFAULT_PORT, ... | 30 ln, ~0.1k tok
types.py | 3 type aliases, 2 classes | UserId, Permissions, Config, ... | 65 ln, ~0.3k tok
__init__.py | package | re-exports: User, Profile | 12 ln, ~60 tok
auth.pyi | stub | 4 classes, 12 functions | 85 ln, ~0.4k tok
conftest.py | 6 fixtures | db_session, client, user_factory, ... | 95 ln, ~0.5k tok
```

**Primary declaration logic:**
1. If one class dominates (>50% of file's methods), show it with bases: `class UserViewSet(ModelViewSet)`
2. If multiple classes, show count: `3 classes`
3. If no classes but functions, show function count: `8 functions`
4. If mostly constants (no classes or functions), show constant count: `5 constants`
5. If type aliases are the primary content, show them: `3 type aliases, 2 classes`
6. If `__init__.py` with `__all__`, show re-exports: `package | re-exports: User, Profile`
7. If `.pyi` stub, prefix with `stub`

The headline shows the most informative summary of what's in the file. Constants, type aliases, and functions all compete for the primary declaration slot — whichever dominates wins.

**Structure:** Indented outline with visibility symbols. Shows the full module shape — classes with their members and variables, top-level functions, constants, and type aliases.

```
# Service for user lifecycle management
class UserService:
  # Create a new service instance
  +__init__(self, db: Database, cache: Cache)               #symbol=UserService.__init__
  # Retrieve user by ID, raising NotFoundError if missing
  +async get_user(self, user_id: int) -> User               #symbol=UserService.get_user
  +async create_user(self, data: UserCreate) -> User        #symbol=UserService.create_user
  -_validate_email(self, email: str) -> bool                #symbol=UserService._validate_email
  ~db: Database (instance)                                  #symbol=UserService.db
  ~cache: Cache (instance)                                  #symbol=UserService.cache

MAX_RETRIES: Final[int] = 3
DEFAULT_TIMEOUT: float = 30.0
type UserId = int | str
+async connect(host: str, port: int) -> Connection          #symbol=connect
-_resolve_host(name: str) -> str                            #symbol=_resolve_host
```

Visibility symbols: `+` public, `-` private (`_name` or `__name`), `~` instance variable. `#symbol=` anchors enable `read("file:///path#symbol=UserService.get_user")`. Async methods show `async`. Type annotations show inline. Docstring summary lines (first line only, per PEP 257) appear as `#` comments above the entity when present — same pattern as C# XML doc `<summary>` extraction. Constants show their type and value. Type aliases show their definition.

### SQL Views

Embedded resource `Schema/python_views.sql`, registered via `IFormatSchemaProvider`.

Three views. Each one is a real entity or relationship that agents navigate — not an inventory of attributes to count.

```sql
-- python_types: "What classes exist? What kind are they?"
-- The entity. Everything else — methods, variables, inheritance — is an
-- attribute of this entity, queryable through joins or properties.
CREATE OR REPLACE VIEW python_types AS
SELECT
    doc.uri AS document_uri,
    n.uri AS type_uri,
    n.properties->>'name' AS name,
    n.properties->>'qualified_name' AS qualified_name,
    n.properties->>'type_kind' AS type_kind,
    n.properties->>'extends' AS extends,
    n.properties->>'metaclass' AS metaclass,
    COALESCE(n.properties->>'is_abstract', 'false') = 'true' AS is_abstract,
    n.properties->>'decorators' AS decorators,
    n.properties->>'docstring' AS docstring,
    n.properties->>'slots' AS slots,
    n.properties->'variables' AS variables,
    n.structure AS structure
FROM node n
JOIN edge e ON e.destination_node_id = n.id
    AND e.type = 'HAS_PART' AND e.is_composition = TRUE
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE n.kind = 'py.type';

-- python_methods: "All async methods", "all methods with @pytest.fixture"
-- Exists for cross-cutting queries that span classes. Methods are attributes
-- of their class, but you need a flat relation to filter across the codebase.
CREATE OR REPLACE VIEW python_methods AS
SELECT
    doc.uri AS document_uri,
    parent.uri AS type_uri,
    parent.properties->>'name' AS type_name,
    parent.properties->>'qualified_name' AS type_qualified_name,
    m.uri AS method_uri,
    m.headline,
    m.properties->>'name' AS name,
    m.properties->>'kind' AS method_kind,
    m.properties->>'accessibility' AS visibility,
    COALESCE(m.properties->>'is_static', 'false') = 'true' AS is_static,
    COALESCE(m.properties->>'is_classmethod', 'false') = 'true' AS is_classmethod,
    COALESCE(m.properties->>'is_async', 'false') = 'true' AS is_async,
    COALESCE(m.properties->>'is_generator', 'false') = 'true' AS is_generator,
    COALESCE(m.properties->>'uses_async_with', 'false') = 'true' AS uses_async_with,
    COALESCE(m.properties->>'uses_async_for', 'false') = 'true' AS uses_async_for,
    COALESCE(m.properties->>'is_generated', 'false') = 'true' AS is_generated,
    COALESCE(m.properties->>'is_overload', 'false') = 'true' AS is_overload,
    m.properties->>'generator' AS generator,
    m.properties->>'parameters' AS parameters,
    m.properties->>'return_type' AS return_type,
    m.properties->>'decorators' AS decorators,
    m.properties->>'docstring' AS docstring
FROM node m
JOIN edge me ON me.destination_node_id = m.id
    AND me.type = 'HAS_PART' AND me.is_composition = TRUE
JOIN node parent ON parent.id = me.source_node_id
    AND parent.kind = 'py.type'
JOIN edge de ON de.destination_node_id = parent.id
    AND de.type = 'HAS_PART' AND de.is_composition = TRUE
JOIN node doc ON doc.id = de.source_node_id AND doc.kind = 'document'
WHERE m.kind = 'py.member';

-- python_imports: "What depends on what?"
-- The dependency graph between modules. Enables "what imports auth?"
-- and "what are this module's runtime vs type-checking dependencies?"
CREATE OR REPLACE VIEW python_imports AS
SELECT
    doc.uri AS document_uri,
    e.properties->>'specifier' AS specifier,
    e.properties->>'names' AS imported_names,
    COALESCE(e.properties->>'is_relative', 'false') = 'true' AS is_relative,
    CAST(COALESCE(e.properties->>'relative_level', '0') AS INTEGER) AS relative_level,
    COALESCE(e.properties->>'is_type_checking_only', 'false') = 'true' AS is_type_checking_only,
    CASE
        WHEN COALESCE(e.properties->>'is_relative', 'false') = 'true' THEN 'internal'
        ELSE 'unknown'
    END AS dependency_type
FROM edge e
JOIN node doc ON doc.id = e.source_node_id AND doc.kind = 'document'
WHERE e.type = 'IMPORTS'
  AND doc.properties->>'language' = 'python';

```

**Everything else is an attribute of these entities:**
- **Inheritance** — `python_types.extends` already has base classes in source order (MRO order). Edge-level ordinals available via `SELECT * FROM edge WHERE type = 'EXTENDS'` for the rare case.
- **Variables** (instance and class) — JSON array on `py.type` nodes, visible in x-ray structure (`~db: Database`).
- **Constants** — JSON array on document nodes, visible in structure text and headlines. Search finds them through artifact text.
- **Type aliases** — JSON array on document nodes, visible in structure text.
- **Top-level functions** — the shared `Functions` view already includes `py.function` nodes.
- **Metaprogramming, framework annotations** — `WHERE kind = 'python.metaprogramming'` or `WHERE kind = 'python.framework'` on the annotation table.

### Error Handling

| Failure | Behavior |
|---------|----------|
| Tree-sitter parse produces ERROR nodes | Skip error regions, extract surrounding structure, emit diagnostic annotation |
| File can't be read (encoding, I/O) | `PipelineResult.Error` with diagnostic |
| Non-UTF-8 encoding | Detect PEP 263 encoding cookie (`# coding=...`) in first two lines. Convert to UTF-8 before parsing. If detection fails, pass through and let tree-sitter handle it with error nodes |
| X-ray summary build fails | Log warning, continue with null headline/structure |
| Decorator name unresolvable | Store raw text, skip semantic mapping |
| `__init__` body too complex to walk | Extract what's accessible, skip rest |
| Type annotation syntax not recognized | Store raw text, no structured parsing |
| Tree-sitter native library missing | Startup failure with clear diagnostic pointing to NuGet package |

Each extraction phase (classes, functions, imports, decorators, docstrings) is independently try/caught. A malformed class definition never prevents function extraction elsewhere in the file.

---

## Cross-Cutting Concerns

**URI addressing:** Python files use `file:///path#symbol=ClassName.method_name` for symbol navigation. Instance methods use `ClassName.method_name`. Class methods and static methods use the same convention — `ClassName.classmethod_name`. Top-level functions use just `function_name`. The `#symbol=` fragment resolves through node qualified_name matching.

**Nested classes and functions:** Python allows classes inside classes and functions inside functions. These are materialized as child nodes of their parent via HAS_PART edges. The `qualified_name` reflects nesting: `OuterClass.InnerClass.method`. The `python_methods` view uses a two-hop join (document → type → member), which correctly handles methods on both top-level and inner classes — inner classes are children of their parent class, and their methods are children of the inner class. For deeply nested classes (3+ levels), the view would miss methods — but practical Python code rarely nests beyond 2 levels. Same limitation as Ruby's views. If needed, a recursive CTE-based view can be added.

**Package init files:** When materialization encounters `__init__.py`, the document node gets `role: "package_init"`. If `__all__` is defined, its contents are stored in `all_exports`. This enables agents to understand a package's public API without reading the init file.

**Namespace packages:** Python supports namespace packages (PEP 420) — directories without `__init__.py` that still function as packages. Namespace package detection requires directory-structure context, not per-file parsing. This is handled in multi-file analysis: directories containing `.py` files but no `__init__.py` are candidates for namespace package annotations. v1 does not detect namespace packages at parse time. Multi-file analysis can emit annotations on directory nodes, or the directory tree view can surface the distinction.

**Type stub files:** `.pyi` files are parsed identically to `.py` files. The document node gets `role: "stub"`. Linking stubs to their implementation modules happens in multi-file analysis — match `foo.pyi` to `foo.py` by filename, merge type information from the stub into the implementation's graph. v1 indexes stubs; linking is an extension point.

**Entry point detection:** `__main__.py` files get `role: "entry_point"`. Files with `if __name__ == "__main__":` guards are not separately classified — this is a code pattern, not a structural role. The guard is visible in content but not surfaced as a node.

**TYPE_CHECKING imports:** Marked on IMPORTS edges as `is_type_checking_only: true`. Agents can filter for runtime-only imports or include type-checking imports depending on their analysis.

**Search integration:** `Artifact.Text` contains the source code and participates in semantic search. Node headlines and structure text make classes, functions, and constants discoverable via explore.

**Generator and async detection:** A function is a generator if its body contains `yield` or `yield from`. An async generator if it's also `async def`. Detection requires walking the function body — tree-sitter queries for `yield` scoped to the function. The parser counts yield sites per function. `async with` and `async for` usage within async functions is similarly detected via scoped queries and surfaced as `uses_async_with` / `uses_async_for` properties.

**Metaprogramming honesty:** When the parser encounters `__getattr__`, `exec`, `eval`, `type()` (3-arg form), `setattr`, or metaclass definitions, it emits a `python.metaprogramming` annotation. Agents query `SELECT * FROM annotation WHERE kind = 'python.metaprogramming'` to understand what the graph might be missing.

---

## Trade-offs

| Chose | Over | Because |
|-------|------|---------|
| Tree-sitter | ANTLR4 | Native indentation handling (no custom lexer base). Research shows tree-sitter-python has 570+ commits, actively maintained. Error recovery produces partial parse trees. Ruby format proves the integration pattern |
| Tree-sitter | External Python process | No Python runtime dependency. "Runs on a developer laptop" without requiring Python installed. Eliminates deployment constraint |
| Tree-sitter | IronPython parser | IronPython stuck at Python 3.4 syntax — no match statements, no type parameters, no modern features |
| Convention-based visibility | No visibility tracking | `_name` and `__name` are how Python developers reason about access. Ignoring them hides information agents use |
| Instance vars from `__init__` only | All `self.x` assignments | `__init__` is the constructor — the canonical place for instance state. Other methods add noise. Clean boundary beats false completeness |
| Decorators as node properties | Separate decorator edges | Decorators are annotations on existing entities, not first-class graph objects. `WHERE decorators LIKE '%route%'` is the natural query. Keeps the graph simple |
| Type annotations as properties | HAS_TYPE edges | Type annotations are properties of the entities they annotate. Text properties keep queries simple. Edge-per-annotation would create an explosion for typed codebases. Matches Ruby's parameter/return_type pattern |
| Class kind from decorators + bases | No kind inference | `@dataclass`, `Enum`, `Protocol` fundamentally change what a class is. Agents need this signal. Heuristic but high value |
| `kind: "method"` for classmethods/staticmethods | `kind: "classmethod"` / `kind: "staticmethod"` | Shared `Functions` view filters on `kind IN ('method', 'function')`. Custom kinds would be silently excluded from cross-format queries. Properties (`is_static`, `is_classmethod`) distinguish them |
| Framework patterns in v1 | Deferring to extension | Python codebases exist in frameworks. An indexer that ignores pytest fixtures, Django fields, and Flask routes misses the highest-value queries. Decorator capture covers most patterns; ORM fields need explicit detection |
| Raw docstring text in v1 | Parsed structured docstrings | Docstring format detection and parsing (Google vs NumPy vs Sphinx) is a significant engineering effort. Raw text is useful immediately — participates in semantic search. Structured parsing is an extension point |
| Variables as JSON on class | Separate variable nodes | Variables are attributes of their class, not independently addressable entities. JSON keeps the graph simple — no node-per-field explosion. Structure text makes them discoverable |
| Conservative dataclass generation | Full generated-method emission | Only `__init__` is generated unconditionally. Other generated methods (`__repr__`, `__eq__`) depend on decorator arguments. Emitting them without parsing args would produce false positives. Extension point |

## Alternatives Considered

**ANTLR4 with python3_13 grammar:** Available, supports latest syntax, pure .NET. Rejected: requires a custom `PythonLexerBase.cs` for indentation handling, which is maintained alongside the grammar. Tree-sitter handles indentation natively. ANTLR4's error recovery is also less granular — error tokens vs. tree-sitter's partial parse trees. ANTLR4 is viable as a fallback if tree-sitter integration proves problematic.

**External Python process (ast.parse):** Always correct (uses CPython's own parser), simple implementation. Rejected: requires Python installed. The TypeScript format establishes this pattern, but TypeScript/Node.js is ubiquitous in JavaScript projects — Python is not guaranteed to be present in all environments where RepoQL indexes Python repos (CI, containers, cross-language repos).

**Pidgin combinator parser:** Pure .NET, no dependencies. Rejected: indentation-sensitive parsing is a fundamental challenge for combinator parsers. No existing Pidgin Python grammar. Implementation effort is prohibitive.

**LibCST/parso via subprocess:** Richer CST (preserves comments, whitespace). Rejected: external Python dependency plus pip install. The additional fidelity over tree-sitter's CST doesn't justify the deployment complexity for v1.

## Risks

| Risk | Mitigation |
|------|------------|
| TreeSitter.DotNet single maintainer | Same risk accepted for Ruby. Grammar source is the official tree-sitter-python (570+ commits, 520+ stars). If the NuGet wrapper is abandoned, grammar and native libraries can be packaged independently |
| tree-sitter-python grammar lags behind CPython trunk | Grammar currently covers through Python 3.12+ including match statements and type parameters. New syntax additions are typically minor. Monitor releases |
| Tree-sitter query authoring effort unprototyped | Python's language surface is larger than Ruby's. The query set hasn't been tested against real Python codebases. Mitigation: prototype query set against a diverse corpus (Django, FastAPI, scikit-learn) before committing to full implementation |
| Decorator semantic mapping incomplete | Conservative: only map built-in Python decorators listed in design. Unknown decorators stored as text but not interpreted. Extension point for framework-specific decorators |
| Class kind heuristic wrong (e.g., custom `Enum` base class) | Uses last component of base class name. False positives possible but rare in practice. `type_kind` is a hint, not a contract — agents can verify by checking the actual base class |
| Instance variable extraction from `__init__` misses state | Deliberate boundary. Documented as a known limitation. Multi-file analysis could extend to `__init_subclass__` or `@dataclass` field declarations |
| Deeply nested classes/functions produce long qualified names | Practical Python rarely exceeds 2 nesting levels. If encountered, extract as-is. Views handle 2 levels; deeper nesting is an edge case |
| `.pyi` stub linking inaccurate | v1 indexes stubs as standalone files. Linking is deferred to multi-file analysis with filename matching |
| Non-UTF-8 Python files produce garbled trees | PEP 263 encoding cookie detection and UTF-8 conversion before parsing. Files without detectable encoding fall through to tree-sitter error recovery |
| Framework pattern detection produces false positives | Medium confidence. Only detect patterns matching well-known dotted names (`models.*`, `db.Column`, `Field`). Agents can filter by confidence |

## Extension Points

- **Structured docstring parsing:** Parse Google/NumPy/Sphinx docstring formats into parameter descriptions, return descriptions, and exception declarations as annotations
- **Framework-specific views:** `django_models`, `flask_routes`, `fastapi_endpoints`, `pytest_fixtures` — convenience views over base graph
- **`.pyi` stub linking:** Multi-file analysis resolves stub files to implementation modules, merging type information
- **Full MRO computation:** UDF that computes C3 linearization given a class and its EXTENDS edges across files
- **Circular import detection:** Multi-file analysis emits annotations for cycles in the IMPORTS graph
- **Star import resolution:** When `from X import *` is encountered, resolve against X's `__all__` to determine actual imports
- **Relative import resolution:** Multi-file analysis resolves relative imports to actual file URIs using package structure
- **ORM field extraction:** Promote ORM field annotations to structured entries in the class's variables JSON with type, constraints, and framework origin
- **Decorator argument parsing:** Extract arguments from decorator calls (e.g., `@app.route("/api/users")` → path `/api/users`; `@dataclass(frozen=True)` → expanded generated methods)
- **`pyproject.toml` cross-format linking:** Cross-format edges between Python packages and their project configuration
- **Namespace package detection:** Multi-file analysis identifies directories with `.py` files but no `__init__.py`
- **Additional dataclass generated methods:** With decorator argument parsing, emit `__repr__`, `__eq__`, `__hash__` based on `@dataclass(...)` options

---

## Project Structure

```
src/Formats/RepoQL.Formats.Python/
    PythonLoader.cs                          # IFormatLoader + IFormatMaterializer + IFormatSchemaProvider
    PythonClassifier.cs                      # IAsyncPipeline<IDiscoveredArtifact, SemanticMediaType?>
    PythonParser.cs                          # IAsyncPipeline<IClassifiedArtifact, Records?>
    PythonDocumentState.cs                   # State transfer between Load and Materialize
    PythonConstants.cs                       # Node kinds, edge types, property keys, media types
    PythonServiceCollectionExtensions.cs     # DI registration
    RepoQL.Formats.Python.csproj             # References: TreeSitter.DotNet, RepoQL.Contracts, RepoQL.Indexing
    Surface/
        PythonDocumentSurface.cs             # Root surface model
        PythonClassInfo.cs                   # Class data (with base classes, decorators, metaclass)
        PythonMethodInfo.cs                  # Method data (with async, generator, decorators, types)
        PythonFunctionInfo.cs                # Top-level function data
        PythonParameterInfo.cs               # Parameter data (name, type, default, kind)
        PythonDecoratorInfo.cs               # Decorator data (name, arguments text)
        PythonImportInfo.cs                  # Import data (module, names, relative level)
        PythonConstantInfo.cs                # Constant data (name, type, is_final) — materialized as JSON on document
        PythonTypeAliasInfo.cs               # Type alias data (name, definition) — materialized as JSON on document
        PythonVariableInfo.cs                # Instance/class variable data (name, type, variable_kind) — materialized as JSON on py.type
        PythonMetaprogrammingHint.cs         # Detected but unextractable patterns
        PythonFrameworkHint.cs               # Framework pattern annotations
        PythonParseStats.cs                  # Parse statistics
        PythonByteRange.cs                   # Byte offset range for spans
    TreeSitter/
        PythonTreeSitterClient.cs            # Tree-sitter wrapper (contains all native interop)
        PythonQueries.cs                     # S-expression query strings
    Schema/
        python_views.sql                     # SQL view definitions

src/tests/RepoQL.Formats.Python.Tests/
    PythonLoaderTests.cs                     # Load + Materialize round-trip
    PythonTreeSitterClientTests.cs           # Parser extraction correctness
    PythonClassKindTests.cs                  # Class kind inference (dataclass, enum, protocol, etc.)
    PythonDecoratorTests.cs                  # Decorator extraction and semantic mapping
    PythonTypeAnnotationTests.cs             # Type hint extraction
    PythonVariableTests.cs                   # Instance and class variable discovery
    PythonImportTests.cs                     # Import extraction and TYPE_CHECKING detection
    PythonDocstringTests.cs                  # Docstring extraction
    PythonVisibilityTests.cs                 # Convention-based visibility
    PythonMetaprogrammingTests.cs            # Metaprogramming detection and honesty annotations
    PythonFrameworkTests.cs                  # Framework pattern detection (ORM fields, etc.)
    PythonConstantTests.cs                   # Module-level constant extraction
    PythonTypeAliasTests.cs                  # Type alias extraction
    PythonAsyncTests.cs                      # Async function, generator, async with/for detection
    PythonConcurrentParsingTests.cs          # Thread-safety of ThreadLocal<Parser>
    Fixtures/
        simple_class.py
        dataclass_example.py
        enum_example.py
        protocol_example.py
        async_functions.py
        async_with_for.py
        decorators.py
        type_annotations.py
        instance_variables.py
        class_variables.py
        imports_basic.py
        imports_relative.py
        imports_type_checking.py
        docstrings.py
        constants.py
        type_aliases.py
        package_init/__init__.py
        metaprogramming.py
        framework_django_model.py
        framework_flask_routes.py
        visibility_conventions.py
        nested_classes.py
        generators.py
        malformed.py
        non_utf8.py
        stub_example.pyi
    RepoQL.Formats.Python.Tests.csproj       # References: TUnit, AwesomeAssertions, FakeItEasy
```

---

*Parse the tree. Respect the types. Be honest about dynamism. Let SQL do the rest.*
