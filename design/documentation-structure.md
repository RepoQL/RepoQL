# Internal Documentation Structure

## Intent

RepoQL should have a small set of durable internal artifacts that act as declarations of intent.

North stars, flows, and designs are permanent and should be treated like code at increasing levels of abstraction. When intent changes, these documents change first, then the implementation is brought into conformance.

Plans are temporary. They exist only to make a design true, then they are deleted.

Research is separate. It informs design, but it is not design.

Public documentation is separate. `RepoQL.Documentation` exists to help people consume RepoQL, not build RepoQL.

## Proposed Structure

```text
/design
  /north-star
    repoql.md
    /system
      trust.md
      token-economics.md
      agent-experience.md
      local-first.md
    /areas
      search.md
      read.md
      query.md
      import.md
      indexing.md
      formats.md
      reliability.md
    /formats
      overview.md
      dotnet.md
      markdown.md
      mermaid.md
      typescript.md
      pdf.md
      python.md
      ...
    /subsystems
      duckdb.md
      uri.md
      embeddings.md
      mcp.md
      dashboard.md

  /flows
    /current
      /repo
      /areas
        indexing/
        search/
        read/
        import/
        formats/
        reliability/
      /formats
        dotnet/
        markdown/
        mermaid/
        typescript/
        pdf/
        python/
        ...
      /subsystems
    /future
      /repo
      /areas
      /formats
      /subsystems

  /designs
    /current
      /repo
      /areas
        indexing/
        search/
        read/
        import/
        formats/
        reliability/
      /formats
        dotnet/
        markdown/
        mermaid/
        typescript/
        pdf/
        python/
        ...
      /subsystems
    /future
      /repo
      /areas
      /formats
      /subsystems

  /plans
    /areas
    /formats
    /subsystems

  /ideas
    /areas
    /formats
    /subsystems

/research

/.claude
  /Skills

/src
  ...
  /RepoQL.Documentation        # public consumption only
  ...
```

## Hierarchy

The primary structure under `design/` should be by document type, not by area.

This preserves the abstraction ladder:

1. `north-star` declares what should be true.
2. `flows` declare how behavior should unfold end to end.
3. `designs` declare what structure should make those flows real.
4. `plans` sequence implementation work.
5. `ideas` capture RepoQL-specific interpretation before commitment.

Areas should appear inside each document type, not replace document types as the main organizing principle.

## North Star Hierarchy

North stars should form a deliberate hierarchy.

### 1. RepoQL

`design/north-star/repoql.md`

The top-level product north star. This declares RepoQL's value proposition and what it should make possible.

### 2. System

`design/north-star/system/`

Repo-wide qualities that cut across everything.

Examples:
- trust
- token economics
- agent experience
- local first

### 3. Areas

`design/north-star/areas/`

Major capability areas.

Examples:
- search
- read
- query
- import
- indexing
- formats
- reliability

These answer: what does great look like for this capability area?

### 4. Formats

`design/north-star/formats/`

Formats deserve their own branch because they are prolific, strategically important, and internally patterned.

Use this branch only where a specific format needs its own excellence bar or special commitments.

Most format work will need flows and designs more often than format-specific north stars.

### 5. Subsystems

`design/north-star/subsystems/`

Only for subsystems that genuinely need their own north star.

Examples:
- DuckDB
- RepoURI
- embeddings
- MCP
- dashboard

These should be rarer than area documents.

## Meaning

| Path | Purpose | Lifetime |
|------|---------|----------|
| `design/north-star` | What should be true | Permanent |
| `design/flows` | How the system should behave end-to-end | Permanent |
| `design/designs` | What structure should make the flows real | Permanent |
| `design/plans` | Temporary execution scaffolding | Deleted when true |
| `design/ideas` | RepoQL-specific interpretation of research, before design commitment | Temporary or promotable |
| `research` | Evidence, comparisons, SOTA analysis, external facts | Keep only while decision-relevant |
| `.claude/Skills` | Internal working guidance for contributors and agents | Living internal guidance |
| `src/**/docs` or local `README.md` | As-built and reference docs owned by code | Living with code |
| `src/RepoQL.Documentation` | Public docs for using RepoQL | Permanent public surface |

## Placement Rules

- If it declares intent for RepoQL itself, it belongs in `design/`.
- If it gathers evidence about the world, it belongs in `research/`.
- If it interprets research for RepoQL but does not yet commit, it belongs in `design/ideas/`.
- If it explains how to work on RepoQL, it belongs in `.claude/Skills` or local project docs, not in public docs.
- If it is reference material for a subsystem, it should live next to the owning code.
- `RepoQL.Documentation` should contain only help for consuming RepoQL.

## Formats

Formats are not just another subsystem. They are a major product surface with many parallel implementations and a growing long tail.

That means format material should exist at two levels:

- `design/north-star/areas/formats.md` for what great looks like for format support overall.
- `design/north-star/formats/*.md` only for formats that need their own specific north star.

And the rest of the ladder should line up naturally:

- `design/flows/current/areas/formats/`
- `design/flows/current/formats/<format>/`
- `design/designs/current/areas/formats/`
- `design/designs/current/formats/<format>/`
- `design/plans/formats/`

## What To Delete

### Delete outright

- `docs/current-state/`
- `docs/findings/`
- `docs/feedback/`

These are not part of the durable design system.

### Delete after migrating anything still valuable

- `docs/north-star/`
- `docs/flows/`
- `docs/designs/`
- `docs/plans/`
- `docs/ideas/`
- `docs/Vision.md`
- `docs/DesignEthos.md`
- `docs/RepoqlDesign.md`

These should be split and re-homed, not carried forward as-is.

### Delete from public documentation and move elsewhere

- `src/RepoQL.Documentation/skills/`
- `src/RepoQL.Documentation/guidance/documentation/`

These are internal authoring and contributor guidance, not public consumption docs.

### Delete originals after moving close to code

- `docs/Embeddings.md`
- `docs/Schema.md`
- `docs/Schema.Design.md`
- `docs/Schema.Markdown.md`
- `docs/RepoQLUri.md`
- `docs/SemanticMediaType.md`
- `docs/XRay.md`
- `docs/Vocabulary.md`

These should become local subsystem docs where they are owned.

## What To Keep And Move

- Keep SOTA and comparative material under `research/`.
- Keep RepoQL-specific interpretation of that research under `design/ideas/`.
- Keep active normative material by rewriting it into `design/north-star`, `design/flows`, and `design/designs`.
- Keep subsystem reference and as-built docs next to the code that owns them.

## Migration Principle

Do not migrate folders wholesale.

Migrate document by document using this filter:

- Keep if it still declares intent or informs a current decision.
- Rewrite if it is valuable but in the wrong form.
- Delete if it is stale, observational, duplicate, or should never have been durable.
