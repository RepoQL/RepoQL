<WHY>
Concepts are source controlled. Record things relevant to the repository, not transient user context.

Your concepts will be required to prove their accuracy, and the proof will be used to keep them accurate over time, so they are a great investment in your future.
</WHY>

<WHEN_TO_USE>
- When you learn a durable idea about this repository that should travel with future agents
- When the user asks you to remember/make a note of/learn something that isn't specific to them personally.
- To capture learnings that could avoid making the same mistake later
- As a map of files relevant to a context

Not this — these belong in your own per-agent memory (not source-controlled), not in a captured concept:
- User identity, preferences, or personal context.
- Conversational asks like "remember this URL for later in this session".
- Anything specific to one user's workflow that other agents in this repo shouldn't inherit.
</WHEN_TO_USE>

<CAPSULES>
Concepts use one shape (capsules) and three categories:

- `wisdom`: timeless ideas. Verification uri is optional. Usually the result of a conversation with the user, but also just abstract realizations you want to remember. Try not to tie them to the task that revealed them too much.
- `rule`: guidelines and invariants future agents should follow. Verification is optional but encouraged — when you give one, it is fact-checked at capture and kept honest over time. Use to apply durable guardrails — these are your repository's road signs.
- `knowledge`: context that improves decisions. Use this for maps of files relevant to a concept, or for general information you want future agents to have. Verification is required to ensure they stay true.
</CAPSULES>

Provide `relevance` as a semicolon-delimited RepoQL URI glob spec such as `file:///src/L2/**;file:///docs/**`. Relevance drives contextual surfacing — the concept is offered when an agent touches a matching file. It stays on disk and is not loaded up front.

`isUniversal` controls whether a concept also loads into the always-on CLAUDE.md concept index that every agent sees regardless of what they touch. Universal is the default — a concept whose frontmatter omits the field is treated as universal — because most concepts are broad enough to be worth knowing up front and the token spend pays off. Set it false (recorded as `universal: false`) only for concepts that apply in a genuinely narrow scope; `relevance` will still surface those in context.
For `knowledge` (and any `rule` you choose to back with evidence), provide `verification` as semicolon-delimited URI globs or web URLs that can fact-check the claim later. Anchor each URI to the proof: `#symbol=NAME` re-resolves to the symbol's current lines as code moves; `#line=M,N` pins a range. The checker reads only the head of each source — at capture and every re-verification — so an unanchored large file can fail a true claim. Whole-file globs still fit claims about a surface's existence or shape. When you provide verification you must also give `ttlDays` — how long the evidence stays trusted before re-checking. Omit `ttlDays` (leave 0) for timeless or unverified concepts; they carry no TTL.

Files are written under `.repoql/concepts/<category>/<subcategory?>/<Name>.md` and are readable through the matching `concept://` URI.

<WISDOM_EXAMPLE>
---
description: "Most design bugs are location bugs — the behavior is right but the address is wrong, move it before you change it"
tags:
  - "design"
  - "engineering"
  - "complexity"
  - "responsibility"
category: wisdom
subcategory: design
relevance: "file://**"

---

## Capsule: LocationTest

**Invariant**
Most design bugs are location bugs — the behavior is right but the address is wrong, move it before you change it

**Example**
The commit writer routed files to the embedding channel. The behavior (routing) was correct. The location was wrong — routing is the hot path's job, not the writer's. Moving it simplified both components without changing any behavior.

**Depth**

- Boundary: If you're about to change what a method does; first ask whether it should exist on this type at all.
- The ownership test: "who calls this?" — if you can't point to exactly one caller; the API is in the wrong place
- Moving a responsibility simplifies both the source (no longer doing someone else's job) and the destination (now has full ownership)
- The fix for "this doesn't belong here" is always moving; not rewriting
- SeeAlso: `DesignSmells`; `CallerMentalModel`; `SlimContracts`
</WISDOM_EXAMPLE>

<RULE_EXAMPLE>
---
description: "Anything doable via MCP must be doable via CLI plus gRPC — the MCP client is one of potentially many"
tags:
  - "architecture"
  - "transport"
  - "parity"
  - "MCP"
  - "CLI"
  - "GRPC"
category: rule
subcategory: architecture
relevance: "file:///src/L3/RepoQL.Hosting.Mcp/**;file:///src/L3/RepoQL.Hosting.App/Commands/**;file:///src/L3/RepoQL.Hosting.Server/**;file:///src/L3/RepoQL.Hosting.Contracts/Protos/**"
verification: "file:///src/L3/RepoQL.Hosting.Mcp/**;file:///src/L3/RepoQL.Hosting.App/Commands/**;file:///src/L3/RepoQL.Hosting.Server/**;file:///src/L3/RepoQL.Hosting.Contracts/Protos/**"
ttl: 90
verified:
  date: "2026-04-29"
  commit: "688db4ab5e9d"

---

## Capsule: TransportParity

**Invariant**
Anything doable via MCP must be doable via CLI plus gRPC — the MCP client is one of potentially many

**Example**
Adding query support means: proto message, gRPC impl, CLI command, MCP handler — all thin mappers to the same `IQueryService`.

**Depth**

- Boundary: If any transport can do something the others cannot; that is a bug.
- gRPC: proto-defined; typed; streamable — the wire contract
- CLI: human-readable text; same operations
- MCP: JSON-RPC over stdio; tool handlers forward to gRPC host
- SeeAlso: `DesirePaths`; `SelfDocumenting`
</RULE_EXAMPLE>

<KNOWLEDGE_EXAMPLE>
---
description: "Host plus client are separate processes — the gRPC host is long-lived plus shared, clients connect plus disconnect freely"
tags:
  - "architecture"
category: knowledge
subcategory: architecture
relevance: "file:///src/L3/RepoQL.Hosting.App/**;file:///src/L3/RepoQL.Hosting.Contracts/**;file:///src/L3/RepoQL.Hosting.Mcp/**"
verification: "file:///src/L3/RepoQL.Hosting.App/**;file:///src/L3/RepoQL.Hosting.Contracts/**;file:///src/L3/RepoQL.Hosting.Mcp/**"
ttl: 90
verified:
  date: "2026-04-29"
  commit: "688db4ab5e9d"

---

## Capsule: TwoProcessArchitecture

**Invariant**
Host plus client are separate processes — the gRPC host is long-lived plus shared, clients connect plus disconnect freely

**Example**
``` Claude Code ←stdio/JSON-RPC→ MCP Client ←gRPC/Unix socket→ Host ``` The host indexes, queries, and serves. The MCP client translates MCP protocol to gRPC calls. Multiple agents can share one host. Starting a new host cooperatively shuts down the existing one.

**Depth**

- Boundary: `dotnet watch` works for the host but not for stdio clients — it contaminates stdout and breaks JSON-RPC.
- Host: long-lived; owns the DuckDB file; runs indexing; serves gRPC — most development happens here
- Client: per-session; speaks MCP over stdio; forwards to host — thin translation layer
- Agents hold leases to keep the host alive; when all leases expire; the host can shut down
- Dev loop: `dotnet watch` for host changes (seconds); `deploy.ps1` for MCP changes (manual reconnect)
- SeeAlso: `TransportParity`; `LayerHosting`
</KNOWLEDGE_EXAMPLE>