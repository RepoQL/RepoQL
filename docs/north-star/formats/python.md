---
description: What great Python format support looks like - declarations for querying Python structure through the knowledge graph
tags: [python, format, north-star, vision]
audience: { human: 50, agent: 50 }
purpose: { north-star: 100 }
---

# Python Format Support: What Great Looks Like

> An agent should be able to understand a Python codebase's classes, functions, imports, type system, and package structure — without reading source files — and query it all through the same SQL surface as every other format.

An agent lands in a Django application with 1,200 Python files. It doesn't open any. It scans headlines and sees: a User model with 8 methods and 3 field declarations, a serializer with nested validation logic, an API view decorated with `@api_view` and `@permission_classes`, a dataclass with 12 typed fields, a utility module exporting 6 pure functions. It asks "what inherits from APIView?" and gets every view class across the codebase. It asks "show me all async functions" and sees not just the definitions but their decorators, parameter types, and return annotations. It asks "what imports the auth module?" and traces the dependency graph from views through serializers to middleware. Where dynamism made structure invisible, the graph said so honestly.

---

## Discovery

- An agent should be able to distinguish file roles from a headline alone — module, package init, test, config, migration, CLI entry point, type stub
- An agent should be able to see what a module defines and what it depends on without opening it
- An agent should be able to tell whether a file uses async patterns, dataclasses, or framework conventions from structure alone
- An agent should be able to see the scale of a module — number of classes, functions, imports — as a filtering signal

```
headline  →  "views.py | class UserViewSet(ModelViewSet) | list, create, retrieve, update | 280 ln, ~1.4k tok"
headline  →  "models.py | 3 classes | User, Profile, Session | 420 ln, ~2.1k tok"
headline  →  "utils.py | 8 functions | validate_token, hash_password, parse_jwt, ... | 150 ln, ~0.7k tok"
headline  →  "__init__.py | package | re-exports: User, Profile | 12 ln, ~60 tok"
```

---

## Classes and Inheritance

- An agent should be able to see all classes in a module with their base classes, metaclasses, and decorator annotations
- An agent should be able to trace the full inheritance chain of any class — including multiple inheritance
- An agent should be able to find all subclasses of a given class across the codebase
- An agent should be able to distinguish class kinds from structure: regular class, dataclass, enum, named tuple, typed dict, protocol, abstract base class
- An agent should be able to see class-level variables, `__slots__`, and their type annotations without reading the file

```sql
-- "What subclasses APIView?"
SELECT * FROM python_types WHERE extends LIKE '%APIView%'

-- "All dataclasses in the project"
SELECT * FROM python_types WHERE type_kind = 'dataclass'

-- "All abstract base classes"
SELECT * FROM python_types WHERE is_abstract = true
```

---

## Functions and Methods

- An agent should be able to see all functions and methods with their full signatures — parameters with types, defaults, and kinds (positional-only, keyword-only, variadic)
- An agent should be able to see return type annotations on every function that has them
- An agent should be able to distinguish between regular functions, async functions, generators, and async generators
- An agent should be able to see method visibility by convention — public, private (`_name`), name-mangled (`__name`)
- An agent should be able to find all methods that implement a dunder protocol — `__enter__`/`__exit__` for context managers, `__iter__`/`__next__` for iterables, `__get__`/`__set__` for descriptors
- An agent should be able to see instance variables discovered from `__init__` alongside explicit class variables
- An agent should be able to see `async with` and `async for` usage as structural annotations on async functions

```
structure →
  class UserService:
    +__init__(self, db: Database, cache: Cache)               #symbol=UserService.__init__
    +async get_user(self, user_id: int) -> User               #symbol=UserService.get_user
    +async create_user(self, data: UserCreate) -> User         #symbol=UserService.create_user
    -_validate_email(self, email: str) -> bool                 #symbol=UserService._validate_email
    ~db: Database (instance)                                   #symbol=UserService.db
    ~cache: Cache (instance)                                   #symbol=UserService.cache
```

---

## Decorators

- An agent should be able to see every decorator applied to a class or function, including its arguments
- An agent should be able to find all functions with a given decorator across the codebase
- An agent should be able to recognize built-in decorators and map them to semantic properties — `@property`, `@staticmethod`, `@classmethod`, `@abstractmethod`, `@dataclass`, `@overload`, `@override`
- An agent should be able to find all uses of any decorator pattern across the codebase — including dotted decorators like `@app.route`, `@pytest.fixture`, `@click.command`

```sql
-- "All pytest fixtures in the project"
SELECT * FROM python_methods WHERE decorators LIKE '%pytest.fixture%'

-- "All route handlers"
SELECT * FROM python_methods WHERE decorators LIKE '%route%' OR decorators LIKE '%api_view%'
```

---

## Type System

- An agent should be able to query type annotations on variables, parameters, and return values as structured data
- An agent should be able to find all uses of a given type across the codebase — as a parameter, return, variable annotation, or base class
- An agent should be able to see generic type parameters on classes and functions (both old-style `TypeVar` and 3.12+ syntax)
- An agent should be able to find explicit type aliases — `type X = ...` (3.12+) and `X: TypeAlias = ...` — and trace them to their definitions
- An agent should be able to see Protocol definitions and list their required methods and signatures
- An agent should be able to query `.pyi` stub files alongside their implementation modules — the type information from stubs enriches the graph for modules that lack inline annotations

---

## Constants and Module-Level Names

- An agent should be able to see module-level variable and constant definitions as part of a module's structure
- An agent should be able to see `Final`-annotated constants as definitively constant
- An agent should be able to see conventionally-named constants (`ALL_CAPS`) with appropriate confidence markers
- An agent should be able to see `__version__`, `__author__`, and other module metadata attributes

---

## Imports and Dependencies

- An agent should be able to see all imports in a module — what is imported, from where, and how (absolute, relative, star)
- An agent should be able to trace the import graph between modules within a project
- An agent should be able to distinguish between internal imports (relative and absolute within-project) and external package imports
- An agent should be able to find circular import chains in one query
- An agent should be able to see `TYPE_CHECKING`-guarded imports distinguished from runtime imports
- An agent should be able to find all consumers of a given module — everything that imports from it
- An agent should be able to see `__all__` exports and know a module's intended public API
- An agent should be able to resolve star imports against the target module's `__all__` when available

```sql
-- "What imports the auth module?"
SELECT source.uri, e.properties->>'specifier'
FROM edge e
JOIN node source ON source.id = e.source_node_id
WHERE e.type = 'IMPORTS' AND e.properties->>'specifier' LIKE '%auth%'
```

---

## Package Structure

- An agent should be able to see the package hierarchy — packages, subpackages, modules — as a navigable tree
- An agent should be able to distinguish regular packages (`__init__.py`) from namespace packages (implicit, no `__init__.py`)
- An agent should be able to see what a package's `__init__.py` re-exports — the package's public API
- An agent should be able to find entry points — `__main__.py` modules and `if __name__ == "__main__"` guards
- An agent should be able to traverse from a Python package to its project metadata — dependencies, entry points, version — through cross-format relationships with `pyproject.toml`

---

## Docstrings

- An agent should be able to see docstrings on modules, classes, and functions as part of structure — not only as content
- An agent should be able to search docstrings semantically alongside code structure
- An agent should be able to see parsed parameter descriptions, return descriptions, and raised exceptions from docstrings that follow standard formats (Google, NumPy, Sphinx)
- An agent should be able to find undocumented public functions and classes in one query

---

## Dynamic Features (Honest Boundaries)

- An agent should be able to see methods generated by recognizable patterns — `@dataclass` fields, `@property` getter/setter/deleter, `NamedTuple` fields, `Enum` members
- An agent should be able to distinguish statically-visible structure from dynamically-generated structure
- An agent should be able to see honesty annotations where the graph cannot represent what exists — `__getattr__` on a class, `exec`/`eval` usage, metaclass-generated members, monkey patching
- An agent should not expect the graph to capture arbitrary runtime behavior — the boundary is patterns with recognizable syntax

```sql
-- "What classes have dynamic attributes the graph can't fully represent?"
SELECT * FROM python_metaprogramming WHERE scope_uri LIKE '%models%'
```

---

## Integrity

- An agent should be able to find files with parse errors and see what structure was recoverable
- An agent should be able to trust that modern Python syntax — match statements, type parameters, f-strings, exception groups — parses correctly
- An agent should be able to find unresolved imports — import specifiers that point to nothing in the project
- An agent should be able to trust that a single malformed file never prevents other files from being indexed
- An agent should be able to distinguish "this file has no structure" from "this file failed to parse"

---

## Framework Conventions

- An agent should be able to find framework-conventional types by their role — Django models, Flask routes, FastAPI endpoints, pytest fixtures, Click commands — through queryable structure
- An agent should be able to see ORM field declarations on model classes as structured data — field name, field type, constraints
- An agent should be able to map test files to the modules they test through naming conventions and import analysis
- An agent should be able to see middleware, signal handlers, and CLI command registrations as graph relationships

---

## What Great Looks Like

| Declaration | Why It Matters |
|-------------|----------------|
| Distinguish module roles from headlines | 1,200 files become navigable in one scan |
| See full function signatures with types | Know a module's API without reading it |
| Trace the import graph across the project | "What depends on X?" answers in one query |
| Query classes by kind — dataclass, enum, protocol | Find the right abstraction mechanism instantly |
| See decorators as semantic annotations | `@property`, `@dataclass`, `@route` change what something is |
| Discover instance variables from `__init__` | A class's state is defined by its constructor |
| Parse docstrings into structured data | Parameter docs enrich the graph, not just the text |
| Find constants and module-level names | The module's contract is more than its functions |
| Know where the graph has gaps | Honest boundaries beat false completeness |
| Trust that parse failures are isolated | One bad file never breaks the index |

---

## Anti-Patterns

| Don't | Declaration Form |
|-------|------------------|
| Read a file to learn what it defines | An agent should see classes and functions from the headline |
| Trace imports by grepping strings | An agent should traverse the import graph |
| Ignore type hints because Python is dynamic | An agent should query type annotations as structured data |
| Flatten inheritance into a list | An agent should trace the full MRO chain |
| Treat `__init__.py` as just another file | An agent should see it as the package's public API |
| Claim completeness when dynamism prevents it | An agent should know what the graph captured versus what it couldn't |
| Ignore decorators | An agent should see `@dataclass`, `@property`, `@abstractmethod` as structural information |
| Treat all functions identically | An agent should distinguish async, generators, class methods, static methods, and properties |
| Let one parse failure cascade | An agent should trust every file is independently indexed |

---

*An agent should be able to understand Python code the way a Python developer does — through classes, functions, decorators, type hints, and package conventions — not the way a parser does.*
