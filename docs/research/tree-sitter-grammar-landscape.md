# Tree-Sitter Grammar Landscape

Research into the publicly available tree-sitter grammar ecosystem: what exists, quality signals, gaps, maintenance characteristics, and alternative approaches.

**Purpose:** Inform RepoQL's tree-sitter format support strategy — what grammars exist, their quality, and where gaps will eventually require authoring our own.

**Date:** 2026-03-09

**Sources:** GitHub API (tree-sitter, tree-sitter-grammars orgs), nvim-treesitter SUPPORTED_LANGUAGES.md, Helix languages.toml, GitHub code-navigation docs, Semgrep docs, Zed docs, blog posts from practitioners (Jake Zimmerman/Sorbet, ahelwer/TLA+, siraben, Pulsar editor series), tree-sitter issues/discussions.

---

## The Ecosystem Structure

### Two Organizations

Tree-sitter grammars are maintained across two GitHub organizations with distinct roles:

| Organization | Repos | Role |
|---|---|---|
| `github.com/tree-sitter` | ~28 active grammar repos | Core languages, created by/for GitHub (originally Atom). C, C++, Python, JavaScript, TypeScript, Go, Rust, Java, Ruby, C#, HTML, CSS, JSON, etc. |
| `github.com/tree-sitter-grammars` | ~86 repos | Curated community bundle. Created Feb 2024. Many are forks moved under central maintenance. YAML, XML, Markdown, Lua, TOML, Zig, Kotlin, HCL, Svelte, Vue, etc. |

There is no explicit published tier system from the tree-sitter project. The distinction is purely organizational.

**tree-sitter-grammars inclusion criteria** (from CONTRIBUTING.md): corpus tests, highlight queries with standard capture names, external scanner in C (not C++), grammar in JavaScript (not TypeScript), active maintainer, SemVer, conventional commits, correct metadata, official CI workflows. **Currently not accepting new third-party contributions.**

Beyond these two orgs, ~225+ grammars are maintained by individual developers, language orgs, and companies (WhatsApp/Erlang, Apple/Pkl, Google/FIDL, Slack/Hack, elixir-lang, nushell, etc.).

### Consumer Catalogs

| Consumer | Grammar Count | Selection Criteria |
|---|---|---|
| tree-sitter wiki "List of parsers" | ~200+ | ABI >= 12, C-only external scanners |
| nvim-treesitter | 328 entries (325 with parsers) | Must correspond to Neovim filetype, feature complete, tested, actively maintained, hosted on GitHub |
| Helix editor | 286 grammar entries | Pinned to exact commit hashes |
| tree-sitter-language-pack (Python) | 165+ | All permissive licenses, pre-built wheels |
| Difftastic | 44+ | Mix of crate deps and vendored parsers |
| Semgrep | 30+ | Extended with Semgrep-specific constructs (ellipsis, metavariables) |
| ast-grep | 23+ | Bundled, extensible via dynamic loading |
| GitHub code navigation | ~20 | "Mature, well-maintained," published to crates.io |
| Lezer (CodeMirror 6) | ~20 | Separate ecosystem, not tree-sitter |

nvim-treesitter and Helix overlap substantially for mainstream languages but each has ~60-70 unique grammars the other lacks.

---

## Quality Dimensions

### How Consumers Assess Quality

No universal standard exists. Each consumer applies different criteria:

- **GitHub**: Requires Linguist inclusion, "mature, well-maintained" parser on crates.io, tag queries. Rejects for: "immature parser," "excessive resources," "low use on GitHub."
- **Neovim**: Tier system (7 stable, 314 unstable, 7 unmaintained, 0 unsupported). Tier 1 requires SemVer releases + WASM artifacts. Only 7 of 328 grammars have achieved Tier 1.
- **Semgrep**: Quantitative — measures parse success rate against most-starred repos for each language. Kotlin initial parse rate: ~98%.
- **Zed**: Grammar repo + git revision per extension. No formal quality bar beyond "it must parse and produce useful trees."
- **Helix**: Pins to exact commit hashes. Only quality signal: explicit exclusion of `wren` and `gemini`.

### Known Quality Issues

| Issue | Detail | Source |
|---|---|---|
| Error recovery | The headline feature, but "wasn't good enough for the cases that mattered most" (autocompletion on incomplete code). Grammar authors have limited ability to influence recovery strategy. | blog.jez.io, tree-sitter #1870 |
| Node type inconsistency | Every grammar defines its own node types as strings with no standardization. "Trying to use TreeSitter to consistently highlight two languages is an exercise in frustration." | HN thread |
| Parser size explosion | SystemVerilog: ~60MB parser.c, 55+ seconds to generate. SQL: 83MB. TypeScript: 8.7MB. | tree-sitter #693, #1041, #1799 |
| External scanner overuse | Required for most real languages. Written in C. "Custom external scanners are a source of a lot of bugs." | tree-sitter docs, Pulsar blog |
| Integer precedence | "Makes some grammars shockingly difficult to maintain." 1.0 checklist proposes pairwise partial orderings. | tree-sitter #930 |
| Grammar version coupling | Emacs 29.x written for mid-2023 grammars. Newer grammars break font-locking and indentation. | emacs-devel mailing list, Jan 2025 |
| ABI version transitions | v0.25 bumped default ABI to 15. Emacs supports 13-14, Zed crashes on >14. | Zed #24632, Doom Emacs #8503 |
| Highlight query fragmentation | Each editor maintains its own query files rather than sharing upstream. | Pulsar blog part 3 |

### Grammars with Specific Quality Notes

- **C# (tree-sitter-c-sharp)**: 29 open issues. Missing C# 12 collection expressions. C# 14 support requested. Lags behind language evolution.
- **C++ (tree-sitter-cpp)**: 30 open issues. Context-sensitivity (type vs variable) requires semantic analysis tree-sitter can't provide.
- **Ruby**: 30 open issues despite active maintenance.
- **Markdown**: Grammar itself disclaims "inaccuracies" and is "not recommended where correctness is important."
- **COBOL**: Explicitly partial — "exists in so many variants and dialects."
- **VHDL/SystemVerilog**: Enormous parsers, struggle with inherently ambiguous formal grammars.

---

## Complete Grammar Inventory

### nvim-treesitter Tier Classification (328 entries)

**Tier 1 — Stable (7):** desktop, editorconfig, inko, python, wit, xresources, zsh

**Tier 3 — Unmaintained (7):** caddy, gdscript, robot, roc, vento, ziggy, ziggy_schema

**Tier 4 — Unsupported (0):** none currently

**Tier 2 — Unstable (314):** Everything else. The full list follows, organized by category.

### By Language Category

#### Systems Programming
| Language | Source Repo | Org |
|---|---|---|
| c | tree-sitter/tree-sitter-c | official |
| cpp | tree-sitter/tree-sitter-cpp | official |
| rust | tree-sitter/tree-sitter-rust | official |
| go | tree-sitter/tree-sitter-go | official |
| zig | tree-sitter-grammars/tree-sitter-zig | curated |
| nim | alaviss/tree-sitter-nim | community |
| odin | tree-sitter-grammars/tree-sitter-odin | curated |
| d | gdamore/tree-sitter-d | community |
| hare | tree-sitter-grammars/tree-sitter-hare | curated |
| c3 | c3lang/tree-sitter-c3 | community |
| v | vlang/v-analyzer | community |
| ada | briot/tree-sitter-ada | community |
| fortran | stadelmanma/tree-sitter-fortran | community |
| pascal | Isopod/tree-sitter-pascal | community |

#### Application/Enterprise Languages
| Language | Source Repo | Org |
|---|---|---|
| java | tree-sitter/tree-sitter-java | official |
| c_sharp | tree-sitter/tree-sitter-c-sharp | official |
| kotlin | fwcd/tree-sitter-kotlin | community |
| scala | tree-sitter/tree-sitter-scala | official |
| swift | alex-pinkus/tree-sitter-swift | community |
| dart | UserNobody14/tree-sitter-dart | community |
| groovy | murtaza64/tree-sitter-groovy | community |
| objc | tree-sitter-grammars/tree-sitter-objc | curated |

#### Scripting Languages
| Language | Source Repo | Org |
|---|---|---|
| python | tree-sitter/tree-sitter-python | official |
| ruby | tree-sitter/tree-sitter-ruby | official |
| javascript | tree-sitter/tree-sitter-javascript | official |
| typescript | tree-sitter/tree-sitter-typescript | official |
| lua | tree-sitter-grammars/tree-sitter-lua | curated |
| perl | tree-sitter-perl/tree-sitter-perl | community |
| php | tree-sitter/tree-sitter-php | official |
| r | r-lib/tree-sitter-r | community |
| bash | tree-sitter/tree-sitter-bash | official |
| powershell | airbus-cert/tree-sitter-powershell | community |
| fish | ram02z/tree-sitter-fish | community |
| zsh | georgeharker/tree-sitter-zsh | community |
| nu | nushell/tree-sitter-nu | community |
| elvish | elves/tree-sitter-elvish | community |

#### Functional Languages
| Language | Source Repo | Org |
|---|---|---|
| haskell | tree-sitter-grammars/tree-sitter-haskell | curated |
| ocaml | tree-sitter/tree-sitter-ocaml | official |
| elixir | elixir-lang/tree-sitter-elixir | community |
| erlang | WhatsApp/tree-sitter-erlang | community |
| fsharp | ionide/tree-sitter-fsharp | community |
| clojure | sogaiu/tree-sitter-clojure | community |
| elm | elm-tooling/tree-sitter-elm | community |
| gleam | gleam-lang/tree-sitter-gleam | community |
| purescript | postsolar/tree-sitter-purescript | community |
| racket | 6cdh/tree-sitter-racket | community |
| scheme | 6cdh/tree-sitter-scheme | community |
| commonlisp | tree-sitter-grammars/tree-sitter-commonlisp | curated |
| fennel | alexmozaidze/tree-sitter-fennel | community |
| nix | nix-community/tree-sitter-nix | community |
| agda | tree-sitter/tree-sitter-agda | official |
| julia | tree-sitter-grammars/tree-sitter-julia | curated |

#### Web Technologies
| Language | Source Repo | Org |
|---|---|---|
| html | tree-sitter/tree-sitter-html | official |
| css | tree-sitter/tree-sitter-css | official |
| scss | serenadeai/tree-sitter-scss | community |
| svelte | tree-sitter-grammars/tree-sitter-svelte | curated |
| vue | tree-sitter-grammars/tree-sitter-vue | curated |
| astro | virchau13/tree-sitter-astro | community |
| angular | dlvandenberg/tree-sitter-angular | community |
| tsx/jsx | tree-sitter/tree-sitter-typescript | official |
| graphql | bkegley/tree-sitter-graphql | community |
| pug | zealot128/tree-sitter-pug | community |

#### Data/Config Formats
| Language | Source Repo | Org |
|---|---|---|
| json | tree-sitter/tree-sitter-json | official |
| json5 | Joakker/tree-sitter-json5 | community |
| yaml | tree-sitter-grammars/tree-sitter-yaml | curated |
| toml | tree-sitter-grammars/tree-sitter-toml | curated |
| xml | tree-sitter-grammars/tree-sitter-xml | curated |
| csv/tsv/psv | tree-sitter-grammars/tree-sitter-csv | curated |
| ini | justinmk/tree-sitter-ini | community |
| hcl/terraform | tree-sitter-grammars/tree-sitter-hcl | curated |
| dockerfile | camdencheek/tree-sitter-dockerfile | community |
| make | tree-sitter-grammars/tree-sitter-make | curated |
| cmake | uyha/tree-sitter-cmake | community |
| proto | coder3101/tree-sitter-proto | community |
| kdl | tree-sitter-grammars/tree-sitter-kdl | curated |
| ron | tree-sitter-grammars/tree-sitter-ron | curated |
| hocon | antosha417/tree-sitter-hocon | community |
| hjson | winston0410/tree-sitter-hjson | community |
| properties | tree-sitter-grammars/tree-sitter-properties | curated |
| editorconfig | ValdezFOmar/tree-sitter-editorconfig | community |
| nginx | opa-oz/tree-sitter-nginx | community |
| ssh_config | tree-sitter-grammars/tree-sitter-ssh-config | curated |
| requirements | tree-sitter-grammars/tree-sitter-requirements | curated |

#### Markup/Document Languages
| Language | Source Repo | Org |
|---|---|---|
| markdown | tree-sitter-grammars/tree-sitter-markdown | curated |
| latex | latex-lsp/tree-sitter-latex | community |
| rst | stsewd/tree-sitter-rst | community |
| typst | uben0/tree-sitter-typst | community |
| djot | treeman/tree-sitter-djot | community |
| vimdoc | neovim/tree-sitter-vimdoc | community |
| bibtex | latex-lsp/tree-sitter-bibtex | community |
| jsdoc | tree-sitter/tree-sitter-jsdoc | official |
| javadoc | rmuir/tree-sitter-javadoc | community |
| phpdoc | claytonrcarter/tree-sitter-phpdoc | community |
| doxygen | tree-sitter-grammars/tree-sitter-doxygen | curated |
| comment | stsewd/tree-sitter-comment | community |

#### Query/Database Languages
| Language | Source Repo | Org |
|---|---|---|
| sql | derekstride/tree-sitter-sql | community |
| graphql | bkegley/tree-sitter-graphql | community |
| sparql | GordianDziwis/tree-sitter-sparql | community |
| ql | tree-sitter/tree-sitter-ql | official |
| prql | PRQL/tree-sitter-prql | community |
| promql | MichaHoffmann/tree-sitter-promql | community |
| kusto | Willem-J-an/tree-sitter-kusto | community |
| jq | flurie/tree-sitter-jq | community |

#### Hardware/Embedded
| Language | Source Repo | Org |
|---|---|---|
| verilog | tree-sitter/tree-sitter-verilog | official |
| systemverilog | gmlarumbe/tree-sitter-systemverilog | community |
| vhdl | jpt13653903/tree-sitter-vhdl | community |
| devicetree | joelspadin/tree-sitter-devicetree | community |
| cuda | tree-sitter-grammars/tree-sitter-cuda | curated |
| glsl | tree-sitter-grammars/tree-sitter-glsl | curated |
| hlsl | tree-sitter-grammars/tree-sitter-hlsl | curated |
| wgsl | szebniok/tree-sitter-wgsl | community |

#### DevOps/Infrastructure
| Language | Source Repo | Org |
|---|---|---|
| dockerfile | camdencheek/tree-sitter-dockerfile | community |
| hcl/terraform | tree-sitter-grammars/tree-sitter-hcl | curated |
| starlark | tree-sitter-grammars/tree-sitter-starlark | curated |
| nix | nix-community/tree-sitter-nix | community |
| just | IndianBoy42/tree-sitter-just | community |
| bitbake | tree-sitter-grammars/tree-sitter-bitbake | curated |
| meson | tree-sitter-grammars/tree-sitter-meson | curated |
| ninja | alemuller/tree-sitter-ninja | community |
| puppet | tree-sitter-grammars/tree-sitter-puppet | curated |

#### Blockchain/Smart Contract
| Language | Source Repo | Org |
|---|---|---|
| solidity | JoranHonig/tree-sitter-solidity | community |
| cairo | tree-sitter-grammars/tree-sitter-cairo | curated |
| circom | Decurity/tree-sitter-circom | community |
| tact | tact-lang/tree-sitter-tact | community |
| sway | FuelLabs/tree-sitter-sway | community |
| move | (in Helix, not nvim-treesitter) | community |

#### Git/VCS
| Language | Source Repo | Org |
|---|---|---|
| git_config | the-mikedavis/tree-sitter-git-config | community |
| git_rebase | the-mikedavis/tree-sitter-git-rebase | community |
| gitattributes | tree-sitter-grammars/tree-sitter-gitattributes | curated |
| gitcommit | gbprod/tree-sitter-gitcommit | community |
| gitignore | shunsambongi/tree-sitter-gitignore | community |
| diff | tree-sitter-grammars/tree-sitter-diff | curated |

*(~100 additional niche/domain-specific grammars omitted for brevity — see nvim-treesitter SUPPORTED_LANGUAGES.md for the complete list)*

---

## Official Tree-Sitter Org Activity

| Repo | Stars | Last Push | Open Issues |
|---|---|---|---|
| tree-sitter-python | 531 | 2025-09-15 | 23 |
| tree-sitter-typescript | 499 | 2025-08-29 | 30 |
| tree-sitter-rust | 480 | 2025-11-24 | 30 |
| tree-sitter-javascript | 465 | 2025-11-24 | 21 |
| tree-sitter-cpp | 407 | 2026-02-25 | 30 |
| tree-sitter-go | 401 | 2026-02-28 | 13 |
| tree-sitter-c | 353 | 2025-11-24 | 30 |
| tree-sitter-c-sharp | 288 | 2026-02-17 | 29 |
| tree-sitter-bash | 286 | 2025-12-02 | 30 |
| tree-sitter-java | 245 | 2025-12-15 | 17 |
| tree-sitter-ruby | 223 | 2026-01-24 | 30 |
| tree-sitter-php | 211 | 2026-02-02 | 1 |
| tree-sitter-html | 198 | 2025-11-24 | 21 |
| tree-sitter-json | 192 | 2025-11-24 | 5 |
| tree-sitter-haskell | 179 | 2025-08-29 | 29 |
| tree-sitter-scala | 179 | 2025-08-07 | 30 |
| tree-sitter-css | 130 | 2025-09-28 | 14 |
| tree-sitter-julia | 122 | 2025-11-08 | — |
| tree-sitter-verilog | 113 | 2024-11-11 | — |
| tree-sitter-ocaml | 90 | 2025-05-31 | — |

**Archived (superseded or abandoned):** tree-sitter-toml (→ tree-sitter-grammars), tree-sitter-swift (abandoned), tree-sitter-tsq (→ tree-sitter-grammars/tree-sitter-query), tree-sitter-razor (WIP since 2016).

---

## Gap Analysis

### Categories Without Grammars

| Category | Examples | Notes |
|---|---|---|
| Mainframe/Enterprise | PL/I, RPG, Natural, MUMPS/M | No grammars found |
| Legacy proprietary | PowerBuilder, Delphi (some exist), Smalltalk | Sparse or abandoned |
| Internal DSLs | Company-specific config languages, ERP scripting | By definition, no public grammars |
| Proprietary vendor formats | SAP ABAP (exists, quality unclear), Salesforce (Apex exists) | Mixed coverage |

### Grammars That Exist But Have Known Issues

| Grammar | Issue |
|---|---|
| C/C++ | Context-sensitivity requires semantic analysis tree-sitter can't provide |
| C# | Lags behind language spec (missing C# 12+ features) |
| Markdown | Self-described as inaccurate, not for correctness-critical use |
| COBOL | Explicitly partial due to dialect fragmentation |
| VHDL/SystemVerilog | Parser size explosion, ambiguous formal grammars |
| GDScript | Marked unmaintained in nvim-treesitter |

### The Long Tail Problem

The tree-sitter ecosystem covers ~300-400 languages comprehensively. However:

1. **The curated orgs are closing ranks** — tree-sitter-grammars is not accepting new contributions. This creates a bottleneck.
2. **Only 7/328 grammars in nvim-treesitter have achieved stable status.** The vast majority track HEAD commits, meaning breaking changes are common.
3. **Each consumer maintains its own query files.** Highlight, fold, indent queries developed in grammar repos diverge from what editors actually use.
4. **Non-editor consumers fork grammars.** Semgrep extends them with pattern constructs. Difftastic vendors them. GitHub pins specific versions. The "same" grammar exists in multiple slightly-different versions.

---

## Authoring and Maintenance

### Authoring Timeline

| Complexity | Timeline | Examples |
|---|---|---|
| Simple/toy | An afternoon | Config formats, simple DSLs |
| Configuration formats | Hours | Straightforward regular structure |
| Real languages with specs | Days to weeks | TLA+ (spec fragmentation was the hard part) |
| Complex production languages | 3-6 months | Languages with context-sensitivity, many edge cases |

### The 90/10 Problem

Getting 90% right is fast. The last 10% — edge cases, error recovery, weird valid syntax — takes longer than the first 90%.

### Maintenance Burden

- **Every grammar change is effectively breaking.** Virtually every merged PR deserves a SemVer major bump.
- **ABI version transitions cause ecosystem-wide pain.** v0.25 (Feb 2025) bumped default ABI to 15, breaking Emacs, Zed, and others.
- **External scanners are maintenance hotspots.** Required for most real languages but are C code with serialization/deserialization bugs.
- **Generated parser.c can be enormous.** Distribution constraints for languages with large grammars (SQL: 83MB, SystemVerilog: 60MB).
- **No standard versioning policy.** Consumers can't easily tell what version of a grammar they're running.

### Existing Conversion Tools

| Tool | Direction | Limitation |
|---|---|---|
| tree-sitter-ebnf-generator | EBNF ↔ tree-sitter grammar.js | Bidirectional, but BNF/EBNF are under-specified |
| antlr2sitter | ANTLR4 → tree-sitter | Direct translation limited; tree-sitter needs precedence, external scanners, error recovery tuning beyond what a CFG specifies |
| Rust Sitter | Rust annotations → grammar | Compile-time conflict detection; incremental parsing not yet implemented |

### LLM-Assisted Grammar Generation

**No evidence found of LLMs successfully generating tree-sitter grammars from scratch.** Tree-sitter is used extensively *as input to* LLMs (AST-based chunking for RAG, structural code understanding), but not the reverse. This appears to be an unexplored space.

---

## Alternative Ecosystems

### Lezer (CodeMirror 6)

Built by Marijn Haverbeke (ProseMirror/CodeMirror). LR parsing with opt-in GLR, incremental reparsing, pure JavaScript. ~20 primary language packages. Deliberately targets web editors, not the same niche as tree-sitter. Grammars are declarative `.grammar` files rather than JavaScript DSLs.

### TextMate Grammars

Regex-based (Oniguruma). Still the backbone of VS Code syntax highlighting. ~30,000+ VS Code extensions. No compilation step. Performance: "even the slowest non-TM engine is approximately 10x faster than TextMate." VS Code issue #50140 tracks tree-sitter support but TextMate remains primary.

### No Real Competitor

No other system approaches tree-sitter's combination of incremental parsing, error recovery, C-based portability, and community grammar coverage.

---

## Non-Editor Uses of Tree-Sitter

Tree-sitter has become foundational infrastructure beyond editors:

| Consumer | How They Use It |
|---|---|
| **GitHub** | Code navigation (40k+ req/min), syntax highlighting migration from TextMate, stack graphs for precise navigation |
| **Semgrep** | Static analysis with extended grammars (ellipsis, metavariables). 30+ languages. |
| **Difftastic** | Structural diff. 44+ syntaxes. Vendors parsers. |
| **ast-grep** | Structural search/lint/rewrite. 23+ bundled grammars. |
| **Aider** | Repository maps for LLM context — extracts symbol definitions. |
| **Cursor/Windsurf/Copilot** | AST-based code chunking for RAG. |
| **Sourcegraph SCIP** | Search-based code navigation. |
| **Topiary** | Universal formatter engine. Formatting rules as tree-sitter queries. |

**Key pattern:** Most non-editor consumers vendor or fork grammars rather than depending upstream directly. This creates fragmentation but also independence.

---

## .NET Integration

| Package | Notes |
|---|---|
| **TreeSitter.DotNet** (NuGet) | 28+ grammars, cross-platform (Win/Linux/Mac, x86/x64/ARM). API: `new Language("JavaScript")`, `new Parser(language)`, `parser.Parse(code)`. |
| **csharp-tree-sitter** | Official C# bindings via P/Invoke. Maintained under tree-sitter org. |

---

## WASM Distribution

Tree-sitter grammars compile to WASM for cross-platform distribution without architecture-specific builds.

Zed's approach is notable: rather than compiling all of tree-sitter to WASM, they extract static parse tables as data and only run lexing logic in WASM. Extensions are sandboxed Wasmtime modules — failures stay contained, extensions reload without restart.

Performance cost exists ("considerably slower than native bindings") but adequate for typical editor workloads.

---

## Quantitative Parse Rates (Semgrep)

Semgrep is the only consumer that publishes quantitative parse success rates. Their methodology: parse top GitHub repos per language (by stars), measure line coverage over 10M+ LOC per language. Biweekly CI runs.

| Language | Parse Rate | Trajectory | Source |
|---|---|---|---|
| Python | 99.998% | Stable at near-perfect | Semgrep v1.104.0 changelog |
| Scala | 99.998% | Restored after regression | Semgrep v1.153 area |
| Julia | 99.3% | "Would qualify for beta on parse rate alone" | Semgrep blog |
| Kotlin | ~98% | Started at 35%, improved through 77%, 90% | Semgrep v0.67-0.68, GA blog |
| C++ | 94.6% | Started at 72.9% | Semgrep ~v0.72 |
| Hack | ~99.9% | — | Slack engineering blog |
| OCaml | 88% | Started at 25% (with pfff parser) | Semgrep v0.60.0 |

**No published rates for:** Java, Go, JavaScript, TypeScript, Ruby, C, C#, PHP, Swift, Rust — despite being GA languages. Dashboard at `dashboard.semgrep.dev` is not publicly accessible.

**Key insight:** Grammars improve dramatically over time when someone actively maintains them. Kotlin went from 35% to 98%. OCaml from 25% to 88%. The starting quality matters less than the maintenance trajectory.

---

## tree-sitter-graph and Stack Graphs

**tree-sitter-graph** (`github.com/tree-sitter/tree-sitter-graph`, 311 stars) is a declarative DSL (`.tsg` files) for constructing arbitrary graph structures from tree-sitter CSTs. You write pattern-matching rules against the syntax tree; for each match, you define what nodes, edges, and attributes to create.

```tsg
(function_definition name:(identifier)@name)@fun_def {
    node def
    attr (def) node_definition = @name
    edge @fun_def.lexical_defs -> def
}
```

**Relationship to stack-graphs:** stack-graphs (`github.com/github/stack-graphs`, 873 stars, **archived**) was the primary consumer. It used `.tsg` files to construct name-resolution graphs for Python (~1,247 lines), Java, JavaScript, TypeScript. GitHub's "precise code navigation" was built on this — and has since been **decommissioned**.

**Relevance to RepoQL:** The `.tsg` approach is conceptually similar to format loaders — declarative rules extracting graph structure from parsed syntax. However:
- **Rust-only, no .NET bindings** — significant integration barrier
- Single-file operation; caller stitches cross-file graphs
- No includes/modules for large `.tsg` files, no user-defined functions, no graph schemas
- The primary consumer (stack-graphs) is archived

**Other consumers:** CodeQL Python extractor, Konveyor C# analyzer, HyperAST, neodepends, joshvera/data-lineage-tracker.

**Related project:** **Ataraxy-Labs/sem** (731 stars) — "Semantic version control CLI. Entity-level diff, blame, graph, and impact analysis. 16 languages via tree-sitter."

---

## tree-sitter 1.0 Roadmap

Current version: **v0.26.6** (Feb 2026). No 1.0 release date set. The 1.0 milestone is labeled "AKA backlog" — 76% complete (26/34 issues closed).

**8 remaining issues:** query caching/serialization, multiple entry points, external scanner improvements (mark_begin, internal regex access), query quantifier bugs, ancestor/descendant query support, and the meta issue itself.

**Already completed:**
- Partial precedence orderings (pairwise instead of integer-only) — shipped v0.19.1
- Reserved words construct (ABI 15) — improves error recovery
- Unicode character properties in regexes
- Native WASM parser support, Rust bindings, ESM support

**Breaking changes in recent releases:** ABI bumped to 15 in v0.25.0 (broke Emacs 13-14 support, Zed crashed on >14). Roughly one major release per year.

---

## LLM-Agent Code Indexing Landscape

A research paper ("Reliable Graph-RAG for Codebases", arXiv 2601.08773) compared three approaches on Java codebases:

| Approach | Build Time | Cost | Reliability |
|---|---|---|---|
| Vector-only RAG | Fast | ~$0.04 | Baseline |
| LLM-extracted knowledge graph | Slow, misses files | ~$0.79 (19.75x) | 377 files lost on Shopizer |
| Deterministic AST-derived graph (DKB) | Seconds | ~$0.09 (2.25x) | Full coverage, multi-hop grounding |

**This directly validates RepoQL's approach** — deterministic graph from parsing beats LLM-extracted graphs on cost, speed, and reliability.

### How Major Tools Use Tree-Sitter

| Tool | Approach | Graph Structure |
|---|---|---|
| **Aider** | Extracts symbol defs/refs, PageRank ranking | NetworkX MultiDiGraph. Repo map fits ~1k tokens (4-6% context). |
| **Cursor** | AST chunking at logical boundaries | Merkle tree for change detection, vector embeddings in Turbopuffer |
| **Continue.dev** | AST parsing for chunking + LSP + vector search | Hybrid: tree-sitter chunking + LSP type crawling |
| **Sourcegraph Cody** | Classifies autocomplete request type | SCIP indexing (precise, not just tree-sitter) |
| **Greptile** | Builds codebase graph + embeddings | Function relationship graph, caller edges added manually |
| **CodeRLM** | Symbol table with cross-references, REST API | Server-based, agent queries on demand (no embedding) |

**RepoQL is unique:** SQL-queryable graph in DuckDB + budget-aware context delivery + transport parity (MCP/CLI/gRPC) + x-ray summaries + representation cascade. No other tool combines these.

---

## Consumer Grammar Curation

### Zed Editor

**Two-tier system:**
- **20 built-in grammars** (compiled natively as Rust crates): Bash, C, C++, CSS, Diff, Go, Go Mod, Go Work, JSDoc, JSON, JSONC, Markdown, Python, Regex, Rust, TSX, TypeScript, YAML, Git Commit
- **Extension grammars** (WASM): ~70+ documented, likely 200-300+ total. Anyone can publish.

**Hybrid WASM architecture:** Parse tables copied from WASM to native memory; only lexing runs in WASM. External scanner heap resets each parse — memory leaks impossible.

**13 known grammar forks** in `zed-industries` org (Python, YAML, SCSS, Zig, Swift, Vue, Nu, Proto, Heex, Go Mod, Go Work, Git Commit, Racket).

### GitHub Code Navigation

**20 languages supported:** Bash, C#, C++, CodeQL, Elixir, Go, JSX, Java, JavaScript, Lua, PHP, Protocol Buffers, Python, R, Ruby, Rust, Scala, Starlark, Swift, TypeScript.

**Quality bar:** Must be in Linguist, "mature, well-maintained" parser published to crates.io, `tags.scm` queries. GitHub explicitly rejects for: "immature parser," "excessive resources," "low use on GitHub."

**Precise code navigation** (via stack-graphs) has been **decommissioned**.

### tree-sitter-grammars CI Quality Gates

From the template repo:
1. Cross-platform tests (Ubuntu, Windows, macOS)
2. Rust binding verification
3. **Fuzzer** on external scanner (if changed)
4. Query validation via `ts_query_ls`

---

## Practitioner Insights (Pulsar Blog Series)

A 7-part blog series by @savetheclocktower (Sep 2023 — Sep 2024) on integrating tree-sitter into the Pulsar editor. Key findings:

**Grammar inconsistency is fundamental:** Same constructs produce wildly different tree structures across parsers. Comments may be a single `comment` node with no sub-structure, or have separate nodes for delimiters. Consumers must write grammar-specific logic.

**Built-in `highlights.scm` files are inadequate for editors.** Too coarse (just `@string`, `@comment`, `@keyword`). Pulsar rejected them entirely and writes custom queries per grammar.

**Error recovery is a black box.** In `tree-sitter-css`, incomplete input `justif` inside a block gets interpreted as an `attribute_name` rather than the expected `property_name`. "I know exactly how I want Tree-sitter to parse the incomplete CSS above, and it's frustrating that I can't just say so."

**Specific grammar quality notes:**
- **CSS**: "Especially susceptible to downsides of Tree-sitter's design decisions"
- **SCSS**: Abandoned (30 months without commits as of mid-2024). Pulsar forked it.
- **Bash**: "A startling act of hubris" to try to parse
- **Markdown**: Had to switch to a different grammar due to quality issues

**The learning curve:** "Easy to start — ten minutes later, adding one rule somehow broke everything." Most crucial decisions happen during lexing, not parsing.

**~45% of parsers use external scanners** — nearly half need custom C code beyond what `grammar.js` can express.

---

## Wiki Parser Catalog

The tree-sitter wiki "List of parsers" contains **~479 parser entries** covering **~320-340 unique languages** (many languages have 2-4 competing parsers). Tracked metadata: name, URL, last commit, ABI version, grammar.json presence, external scanner usage.

**ABI distribution:** ~83% are ABI >= 14. ~90-100 entries already on ABI 15 (the newest).

**~100+ parsers on the wiki are not in nvim-treesitter or Helix** — niche/domain-specific languages: cfengine, cooklang, fluentbit, foam, ibmhlasm, poe_filter, twitchchat, vespa, chuck, faust, lilypond, supercollider, bazelrc, bison, etc.

---

## Lessons from GitHub's Three Failed Attempts

GitHub tried to build multi-language code intelligence three times. All three are now archived. The pattern is the most important finding in this research.

### The Arc

| System | Years | Language | Approach | Outcome |
|---|---|---|---|---|
| **Semantic** | 2015-2025 | Haskell | Full program analysis framework (parsing, diffing, abstract interpretation) | Archived Apr 2025. 34,713 commits. |
| **Stack-graphs** | 2021-2025 | Rust | Focused name resolution via scope graphs formalism | Archived Sep 2025. 2 languages reached production. |
| **Tree-sitter tag queries** | ~2022-present | Tree-sitter DSL | Simple `tags.scm` pattern matching for definitions/references | **The only survivor.** |

Each successor was **simpler** than the last. The trend was toward less precision, more coverage, simpler maintenance. The system that survived is the simplest possible approach.

### Why Semantic Failed

From the ACM paper "Static Analysis at GitHub" (2022): *"Though Semantic performed well, using the Tree-sitter query language allowed faster iteration and avoided the operational overhead of a program-analysis framework."*

- **Per-language Haskell code was a scaling bottleneck.** Before CodeGen (2020), each language required two hand-written grammars that had to stay synchronized — one for tree-sitter, one in Haskell parser combinators. Even after CodeGen automated half, each language still needed Haskell integration work.
- **Full program analysis was overkill.** Abstract interpretation ambitions never reached production. The team spent years building capabilities the product didn't need.
- **Community couldn't contribute.** Haskell + complex architecture = no external language contributions reached production quality.
- **Operational overhead** of running a Haskell runtime at 40k+ req/min.

### Why Stack-Graphs Failed

After 4+ years of development, only **Python and TypeScript** achieved precise navigation in production. Java had `.tsg` rules written but never shipped.

**Per-language effort didn't scale:**
- The `.tsg` DSL for defining name binding rules had a steep learning curve
- No community-contributed languages reached production quality
- External contributors couldn't even get the scaffolding to compile (issues #462, #424)

**Formalism limitations emerged late:**
- Type-dependent name resolution (Solidity multiple inheritance via C3 linearization) was impossible syntactically
- Scope graphs can't handle Hindley-Milner type inference
- Macro expansion obscures scope structure
- Build-system-dependent resolution (C++ `#include`, Java classpath, Go modules) was architecturally excluded by the zero-config design

**Even supported languages had bugs:**
- Python cross-module resolution was broken (issues #430, #464)
- The maintainer acknowledged: *"I don't quite remember if this is on purpose or an oversight in the rules"*

**Key people left GitHub:**
- Douglas Creager (lead) → now at Astral (ruff, uv, ty)
- Hendrik van Antwerpen (primary maintainer) → now at nuanced-dev
- No one remained who understood the system deeply

**AI displaced traditional analysis:**
- GitHub "re-founded on Copilot" at 2023 Universe
- 2023 layoffs (10% of workforce) coincide with Semantic dropping to 1 commit that year
- Precise-but-slow traditional code analysis couldn't compete for resources

**Third-party assessment** (Aider issue #534, Oct 2024): *"It's a super promising project that's not quite ready for primetime. Unless there is a team dedicated to active development of stack-graphs, it would be very hard to get value from integrating."*

### The Pattern That Repeats

**Analytical depth loses to iteration speed and per-language onboarding cost. Every time.**

| Dimension | What GitHub Learned | RepoQL Implication |
|---|---|---|
| Per-language effort | 4 years → 2 languages in production. The bottleneck is authoring, not parsing. | Format loaders must be fast to write. If a new language takes weeks of C# work, the same bottleneck emerges. |
| Zero-config vs accuracy | Refusing build system info made C++ includes, Java classpath impossible. | RepoQL's "zero to explorable" constraint has the same tension. Accept graceful degradation — 80% accuracy with zero config beats 100% accuracy requiring project setup. |
| Formalism rigidity | Scope graphs couldn't handle type-dependent resolution, macros, multiple inheritance. | Don't over-formalize. RepoQL's graph (nodes + edges) is deliberately loose. Better to emit approximate edges than fail to emit any. |
| Community contributions | Complex DSL = no external contributions. | Format loaders in C# are more accessible than `.tsg` files or Haskell. But they still require understanding the pipeline. |
| Key person risk | When the architect leaves, the project dies. | Document the *why* not just the *what*. |
| "Not quite ready" stays that way | Chicken-and-egg: without enough languages, no adoption; without adoption, no contributors. | Ship incomplete language support early. A format loader that extracts types and functions but misses edge cases is infinitely more valuable than no loader at all. |
| AI competition | Copilot got all the oxygen. Precise analysis couldn't justify its cost. | RepoQL positions as "conventional software that makes AI more capable." This is the right frame — complement AI, don't compete with it. |
| Cross-file is the hard problem | Single-file analysis worked. Cross-module resolution broke. | Edges between files are where value and difficulty both concentrate. Get intra-file structure right first, cross-file relationships iteratively. |

### What Survived from GitHub's Attempts

| Artifact | Status |
|---|---|
| tree-sitter `tags.scm` approach | **Alive.** The simplest approach won. |
| tree-sitter field API (named child nodes) | Merged upstream. Used by all grammars today. |
| fused-effects (Haskell library) | 667 stars, still maintained. |
| tree-sitter-graph DSL | Active (Rust). Used by CodeQL Python extractor. |
| Scope graphs research | Lives on in academia and nuanced-dev. |

---

## What We Could Not Determine

1. **Semgrep's complete per-language parse rates.** Dashboard not publicly accessible. Only 7 languages have published numbers.
2. **Whether any LLM has successfully generated a non-trivial tree-sitter grammar.** No published evidence found.
3. **GitHub's internal quality metrics** beyond "mature, well-maintained."
4. **Long-term bitrot rates** — no quantitative data on grammar breakage from language spec changes.
5. **Zed's complete language extension count** — likely 200-300+ but couldn't enumerate.
6. **Specific patches in Zed's 13 grammar forks** — would require diffing each against upstream.
7. **tree-sitter 1.0 timeline** — no date set, milestone treated as backlog.

---

## Leads Still Open

- **Ataraxy-Labs/sem** (731 stars) — entity-level diff/blame/graph across 16 languages. Comparable approach to RepoQL's graph extraction.
- **CocoIndex** — open-source real-time codebase indexing with tree-sitter. Rust-based, incremental.
- **Tree-sitter MCP Server** (PulseMCP) — MCP server exposing tree-sitter parsing directly to agents.
- **Semgrep's CircleCI artifacts** — biweekly `stat.txt` files with parse rates for all 32 tested languages (not committed to repo).
- **CodeQL's Python extractor** (`github/codeql/python/extractor/tsg-python/`) — production-grade use of tree-sitter-graph outside stack-graphs.
- **Pulsar's "scope tests" pattern** — custom query predicates for handling grammar inconsistencies. Potentially useful for RepoQL's format system.
