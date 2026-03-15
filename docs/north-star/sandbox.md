# Sandbox: What Great Looks Like

> An agent should be able to build its own tools — in JavaScript, inside the sandbox — and have them remembered.

An agent is analyzing a monorepo with a custom release process. It writes a 30-line JavaScript module that parses every `CHANGELOG.md` in the repo, cross-references versions against the dependency graph, and flags packages whose dependents haven't updated. It registers the module once. Next week, a different agent — working in the same repo, with no memory of the first — imports that module by name and runs the same analysis. The tool remembered.

---

## Capabilities

- An agent should be able to read content by URI from within a sandbox script — files, graph data, documentation, structured views
- An agent should be able to write content by URI to scoped locations from within a sandbox script
- An agent should be able to delete content by URI within scoped locations
- An agent should be able to express all sandbox operations through these three primitives — read, write, delete — uniformly addressed by URI
- An agent should be able to use a default scratch space for temporary output without any configuration

---

## Scoping

- An agent should be able to trust that writes only succeed within explicitly configured scopes
- An agent should be able to see exactly what scopes are allowed when a write is denied
- An agent should be able to use a temporary directory under `.repoql/` for scratch output without configuring write scopes
- An agent should be able to trust that read access is bounded to the repository and its indexed content

---

## Module Authoring

- An agent should be able to write a JavaScript module, register it, and have it available in every future session
- An agent should be able to import a module it authored with the same syntax it uses for bundled libraries
- An agent should be able to get feedback at registration time that tells it exactly what to fix — not silent acceptance, not opaque failure
- An agent should be able to list all available modules — bundled and agent-authored — through the same surface
- An agent should be able to delete or replace a module it previously created
- An agent should be able to find documentation for any module through `help://`, because registration requires documentation

---

## Module Trust

- An agent should be able to tell whether a module is bundled or agent-authored
- An agent should be able to trust that agent-authored modules cannot shadow bundled ones
- An agent should be able to inspect a module's provenance — who created it, when, what it requires — before running it
- An agent should be able to identify modules that no longer work — stale references, missing dependencies, broken code
- An agent should be able to trust that naming conflicts between modules are detected at registration, not at runtime

---

## Capability Injection

- An agent should be able to trust that modules only access capabilities they were explicitly given
- An agent should be able to use a module's pure functions without providing capabilities — the same module works in SQL `js()` for computation and in the sandbox tool for orchestration
- An agent should be able to get a clear error when a module function requires capabilities that the current context does not provide

---

## Module Sharing

- An agent should be able to install a module published by someone else and use it immediately — same syntax, same safety guarantees
- An agent should be able to trust that a community module runs inside the same sandbox as any other code — no escape, no privilege escalation, regardless of who wrote it
- An agent should be able to inspect what a module requires before installing it — what capabilities it needs, what scopes it accesses
- An agent should be able to publish a module for others to use without any toolchain beyond the sandbox itself
- An agent should be able to trust that a module's declared capabilities are enforced — a module that claims to only read cannot write, even if the code tries

---

## Module Composition

- An agent should be able to import bundled libraries from within an agent-authored module
- An agent should be able to combine bundled data-format libraries with graph access to build domain-specific tools
- An agent should be able to trust that module dependencies cannot create circular or unbounded chains

---

## Debugging

- An agent should be able to emit diagnostic messages from within a sandbox script and see them in the output
- An agent should be able to express urgency in diagnostic output — distinguish routine progress from warnings from errors
- An agent should be able to debug a module it wrote last week using the same diagnostics it uses in one-off scripts
- An agent should be able to see diagnostics alongside results, not in a separate channel

---

## Output Quality

- An agent should be able to trust that sandbox results are formatted identically to every other RepoQL tool — indistinguishable in shape and polish
- An agent should be able to receive structured results for programmatic use and formatted results for display, always both
- An agent should be able to trust that errors from the sandbox follow the same structured format as errors from any other tool

---

## Safety

- An agent should be able to trust that expanded capabilities do not weaken sandbox isolation — memory limits, statement budgets, and timeout guarantees apply identically whether a script uses capabilities or not
- An agent should be able to trust that capability calls from within a script do not consume the computation budget — a read or query counts as one operation, not thousands of statements
- An agent should be able to trust that a script cannot exceed its configured scopes, regardless of what code it runs
- An agent should be able to trust that the capability surface is immutable at runtime — scripts cannot modify, replace, or escalate it
- An agent should be able to trust that errors from capabilities are catchable — handle them or let them bubble, but never silent failures

---

## Two Surfaces, One Engine

- An agent should be able to use `js()` in SQL for pure computation — no capabilities, no side effects
- An agent should be able to use the sandbox tool for orchestration — capabilities, scoped filesystem, graph access
- An agent should be able to import the same module in both contexts — pure functions work in SQL, capability-dependent functions work in the sandbox tool
- An agent should be able to trust that SQL `js()` will never gain capabilities — the boundary between computation and orchestration is permanent
- An agent should be able to get an actionable error when a capability-dependent function is called in a context that does not provide capabilities

---

## Plugins

- An agent should be able to use modules backed by native-speed processing without leaving the sandbox — media conversion, image analysis, custom parsers, all running inside the same isolation boundary
- An agent should be able to import and use a plugin with the same syntax as any other module — the consumer does not need to know what language a module was compiled from
- An agent should be able to trust that plugins cannot escape the sandbox — no native code execution, no ambient authority, same capability scoping as any other module

---

## What Great Looks Like

| Dimension | Great | Acceptable | Unacceptable |
|-----------|-------|------------|--------------|
| **Capabilities** | Read/write/delete by URI, uniform, with default scratch space | Separate APIs for graph vs filesystem operations | Different paradigms for different data sources |
| **Modules** | Register once, available forever, discoverable, documented, shareable | Register once per session, auto-loaded from file | Redefine every session from scratch |
| **Sharing** | Install a community module and trust it runs safely — same sandbox, same scopes | Sharing possible but requires manual file copying | No sharing; every repo builds from scratch |
| **Trust** | Provenance visible, capabilities declared and enforced, conflicts caught at registration | Metadata available via command | Modules are opaque until they fail |
| **Safety** | Isolation identical with or without capabilities; scopes enforced unconditionally | Isolation weakened but bounded when capabilities are used | Capabilities bypass sandbox limits |
| **Output** | Indistinguishable from native tools — same shape, same polish | Recognizably similar to native tools | Visibly different format, missing footer |
| **Two Surfaces** | Permanent boundary — `js()` never gains capabilities, clear error at the boundary | Boundary maintained by convention | Capabilities leak into SQL surface |
| **Plugins** | Native-speed processing inside the sandbox, same isolation | Plugins run with relaxed isolation but bounded | Plugins escape to native code |

---

## Anti-Patterns

| Don't | Why | Do Instead |
|-------|-----|------------|
| Give modules ambient access to capabilities | Modules that silently use capabilities are untestable and opaque | Modules only access capabilities explicitly provided to them |
| Allow unbounded module dependency chains | Dependency graphs, versioning, circular deps — complexity virus | Module dependencies are bounded and acyclic by construction |
| Accept module registration silently | Bad modules discovered at use time, not definition time | Validate at registration with specific, fixable feedback |
| Make sandbox output look different from other tools | The sandbox feels bolted on, not native | Same shape, same formatting, same error structure |
| Allow writes without scope bounds | One bad script overwrites production code | Default scratch space available; broader writes require explicit scoping |
| Add capabilities to SQL `js()` | Blurs the computation/orchestration boundary permanently | Two surfaces: `js()` for computation, sandbox tool for orchestration |
| Let plugins escape the sandbox | Safety is real or it isn't — partial isolation is no isolation | All plugin code runs inside the sandbox's isolation boundary |

---

## The Accumulation Effect

The most important property of agent-authored modules isn't convenience — it's **accumulation**. Each agent session can leave behind a tool. A module written to debug a release process becomes the standard release checker. A module written to generate a report becomes the canonical report generator. A module written to validate configurations becomes the validation layer.

Without authoring, every agent improvises from scratch. With it, every agent inherits the tools of every agent before it. And because modules are shareable, the accumulation extends beyond a single repo. A module that solves dependency analysis in one codebase can be published, installed in another, and trusted to run safely — because the sandbox guarantees hold regardless of who wrote the code. The ecosystem grows from individual tools to shared infrastructure, all running inside the same isolation boundary.

---

*An agent should be able to build the tool it needs, inside the tool it already has.*
