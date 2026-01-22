# RepoQL.Formats.Markdown

Markdown loader + analyzer for RepoQL with X‑ray summaries via Fluid (Liquid) templates.

## What it does

- Parses Markdown using Markdig with the following extensions:
  - YAML frontmatter, tables (pipe/grid), autolinks, task lists, emphasis/list/definition list extras, media links.
- Extracts structure (“surface”):
  - Headings: level, text, slug, precise source spans.
  - Links: href, title, link text, precise spans, local anchor resolution.
  - Code blocks: fenced and indented, language, arguments, line counts.
- Builds X‑ray fields on the Artifact using embedded Liquid templates:
  - `headline`: single line summary including title (frontmatter or H1), size/lines, top code language, up to 2 frontmatter tags, and 2–3 key topics (H2/H3).
  - `summary`: short multi‑line description (type, size/lines, counts for sections/code/links/images/tables, frontmatter key count).
  - `structure`: compact outline of headings (max 25 lines).
- Lints intra‑document and cross‑document anchors in `MarkdownAnalyzer`.
  - Verifies local `#slug` anchors exist.
  - If a link targets another document, resolves and validates the target anchor.
- Supports embedded fragment analysis: fenced code blocks are detected and, when a language label maps to a registered format, analysis is delegated to the corresponding analyzer and results are remapped to the parent document.

## X‑ray templates

Embedded under `Templates/explore` and included as resources:

- `explore/headline.liquid`
- `explore/summary.liquid`
- `explore/structure.liquid`

Model keys available to templates:

- `file_name`, `media_kind`, `media_base`, `size_bytes`, `line_count`
- Counts: `headings_count`, `codeblocks_count`, `links_count`, `images_count`, `tables_count`, `frontmatter_keys`
- Code metadata: `code_lang_counts`, `top_lang`
- Semantics: `title`, `topics` (first 2–3 H2/H3), `tags` (up to 2 frontmatter tags)
- `headings`: array of `{ level, text, indent }`

Example headline output:

```
Authentication — readme.md | markdown.doc | 4.2 KB | 120 lines | lang: csharp | #auth #oauth | topics: Overview, Token Flow, Refresh
```

Example summary output:

```
Type: markdown.doc
Size: 4.2 KB, Lines: 120
Sections: 8, Code: 2, Links: 5, Images: 1, Tables: 1
Frontmatter: 3 keys
```

Example structure output (first 25 headings):

```
- Title
  - Getting Started
  - Usage
  - FAQ
```

## Configuration

Templates are loaded via `LiquidTemplateRenderer` using `EmbeddedFileProvider` scoped to `RepoQL.Formats.Markdown.Templates`. To override or extend templates, either:

- Add a project reference to `RepoQL.Templating` and register a custom `ITemplateRenderer` in DI, or
- Pass a custom `ITemplateRenderer` into `MarkdownLoader`.

## Notes

- X‑ray is best‑effort: if template rendering fails, indexing continues and the fields are left null.
- Keep non‑headline summaries terse; detailed structure stays in `structure`.
