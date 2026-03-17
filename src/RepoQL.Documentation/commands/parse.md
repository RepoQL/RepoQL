---
description: "Parse a file through the hot-path pipeline and display its representations without persisting."
tags: ["command", "parse", "preview", "format", "test"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::parse

Parse any file through the indexing pipeline and show the resulting representations. Does not persist results — useful for testing format support and inspecting what RepoQL extracts.

---

## Capsule: Views

**Invariant**
Three views, increasing detail: overview (shape), graph (structure), records (database).

**Example**
```
::parse[path]            → Overview: summaries, graph counts, stage timings
::parse[path, graph]     → Composition tree with node kinds, headlines, span lines
::parse[path, records]   → Every record that would be written to the 5 tables
```

**Depth**
- **Overview** (default) — did it parse? What media type? What do the x-ray summaries look like? How many nodes/edges by category?
- **Graph** — walks composition edges to build a containment tree. Each node shows kind, headline, and line range. Non-composition edges (CALLS, REFERS_TO) listed separately. Annotations with messages.
- **Records** — mirrors the 5 database tables (artifact, node, edge, span, annotation). Nodes indexed as N0..Nn, spans as S0..Sn, edges as E0..En for cross-referencing. Shows props_json, span byte/line ranges, edge src/dst with composition flags.

---

## Capsule: BasicUsage

**Invariant**
`::parse[path]` reads the file, runs it through the hot-path pipeline, and returns artifact summaries, graph statistics, and stage timings.

**Example**
```
::parse[C:\Source\MyProject\src\Foo.cs]
→ Parsed: Foo.cs (csharp.source, 2,340 bytes)

  Headline:
  Foo.cs — FooService class with dependency injection

  Structure:
  namespace MyProject.Services
    class FooService : IFooService
      ctor(ILogger, IRepository)
      async Task<Result> ProcessAsync(Request)
      void Validate(Request)

  Graph:
    8 nodes, 12 edges, 6 spans, 2 annotations
    Nodes: document(1), namespace(1), type(1), callable(3), parameter(2)
    Edges: HAS_PART(5), CALLS(3), REFERS_TO(2), PARAMETER_OF(2)
    Annotations: outline(1), metrics(1)

  Stages:
    Discovery    2ms
    Parse       18ms
    Summarize   12ms
    Embed        0ms [Skipped]
```

**Depth**
- Accepts any absolute file path — does not need to be inside the active repository
- File content is sent to the host; the file name determines format detection
- Results are not persisted to the database — purely diagnostic
- Stage timings show where time is spent in the pipeline
- Failed stages show error details inline

---

## Capsule: GraphView

**Invariant**
`::parse[path, graph]` renders the node graph as a composition tree with reference edges and annotations shown separately.

**Example**
```
::parse[C:\Source\MyProject\src\Foo.cs, graph]
→ Parsed: Foo.cs (csharp.source, 2,340 bytes)

  Composition tree:
    document         Foo.cs                              L1-45
      namespace      MyProject.Services                  L3-44
        type         FooService : IFooService            L5-44
          callable   .ctor(ILogger, IRepository)         L8-12
          callable   ProcessAsync(Request)               L14-30
          callable   Validate(Request)                   L32-43

  References:
    CALLS            ProcessAsync → Validate  L22
    REFERS_TO        .ctor → ILogger  L9

  Annotations:
    [outline] info  namespace MyProject.Services...
    [metrics] info  complexity: 12, lines: 40
```

---

## Capsule: RecordsView

**Invariant**
`::parse[path, records]` shows every record that would be written to the database, with cross-referencing indices.

**Example**
```
::parse[C:\Source\MyProject\src\Foo.cs, records]
→ Parsed: Foo.cs (csharp.source, 2,340 bytes)

  Artifacts (1):
    media_type=csharp.source  size=2,340
      headline: Foo.cs — FooService class with dependency injection
      structure:
        namespace MyProject.Services
          class FooService : IFooService
            ...

  Nodes (8):
    N0    document          Foo.cs                                 span=S0
    N1    namespace         MyProject.Services                     span=S1
    N2    type              FooService : IFooService                span=S2
    N3    callable          .ctor(ILogger, IRepository)             span=S3
          props: {"access":"public","is_constructor":true}
    ...

  Edges (12):
    E0    HAS_PART          N0 → N1 composition
    E1    HAS_PART          N1 → N2 composition ord=1
    E2    CALLS             N4 → N5 src_span=S6
    ...

  Spans (6):
    S0    L1-45        bytes 0-2340
    S1    L3-44        bytes 52-2300
    ...

  Annotations (2):
    [outline] info  source=csharp-parser
      namespace MyProject.Services...
    [metrics] info  source=metrics
      complexity: 12, lines: 40
```

---

## Help

```
::parse --help
→ ::parse — Parse a file and show its representations

  Usage: ::parse[path, view?]
    path  Absolute file path
    view  View: overview (default), graph, records
```
