# Sandbox Platform Plans

Implements: [Sandbox Platform Design](../../designs/future/sandbox-platform.md)

## Plan Sequence

| # | Plan | What it delivers |
|---|------|-----------------|
| 01 | [Foundation Contracts](01-foundation-contracts.md) | `ISandboxContentReader`, `IWritableFileSystem`, repo-rooted scope model |
| 02 | [Capability Injection](02-capability-injection.md) | `repoql` global with `read()`, scope enforcement, statement counter pause, gRPC contract |
| 03 | [Output Formatting](03-output-formatting.md) | Result + diagnostics + footer matching RepoQL's standard shape |
| 04 | [Write and Delete](04-write-delete.md) | `repoql.write()`, `repoql.delete()`, default scratch space |
| 05 | [Module Registry](05-module-registry.md) | `.repoql/modules/`, manifest, validation, `::module.*` commands, capability declarations |

Plans 01-02 are the critical path. Each subsequent plan delivers standalone value.
