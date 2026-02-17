---
description: Research into Roslyn analyzers for RepoQL — craft, convention enforcement, and broader landscape
tags: [roslyn, analyzers, source-generators, conventions, testing]
audience: { human: 50, agent: 50 }
purpose: { research: 80, reference: 20 }
---

# Roslyn Analyzers for RepoQL

Research for: what analyzers to build, how to build them well, and what the broader landscape enables.

*Research date: 2026-02-17*

## Context

RepoQL has a new `src/RepoQL.Analyzers/` project targeting `netstandard2.0` with one shipped analyzer:

- **RQL001** (`UseCorrectTestFrameworkAnalyzer`): Detects xUnit `[Fact]`/`[Theory]`/`[InlineData]`, suggests TUnit equivalents. Uses syntax-first, semantic-fallback pattern. Applied globally via `Directory.Build.props`.

The codebase enforces conventions documented in CLAUDE.md (single writer, frozen schema, TUnit, AwesomeAssertions, attribute-driven auto-discovery) that are currently only enforced by review. Several of these are detectable at compile time.

---

## Part 1: The Craft

### Analyzer Types

| Component | Purpose | When it runs |
|-----------|---------|-------------|
| `DiagnosticAnalyzer` | Inspect code, report problems | Continuously in IDE + at build time |
| `CodeFixProvider` | Fix reported problems | On-demand (lightbulb) |
| `IIncrementalGenerator` | Generate source at compile time | During compilation |
| `DiagnosticSuppressor` | Suppress diagnostics from other analyzers | With the suppressed analyzer |

`ISourceGenerator` (non-incremental) is obsoleted in practice. The Roslyn team: "fundamentally not scalable... should literally just never be used."
> [dotnet/roslyn#65106](https://github.com/dotnet/roslyn/issues/65106)

`CodeFixProvider` is loosely coupled to analyzers via diagnostic ID string only — one fix can address diagnostics from any analyzer.
> [Microsoft Learn tutorial](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/tutorials/how-to-write-csharp-analyzer-code-fix)

### Action Registration

| Action | Fires on | Performance | Best for |
|--------|----------|------------|---------|
| `RegisterSyntaxNodeAction` | Each matching `SyntaxKind` | Cheapest (syntax only) | Syntax checks, semantic fallback when name matches |
| `RegisterSymbolAction` | Symbol declaration completion | Medium | Declaration-level rules (naming, inheritance) |
| `RegisterOperationAction` | Each matching `IOperation` | Medium (semantics pre-baked) | Semantic analysis of executable code |
| `RegisterCompilationStartAction` | Once before other actions | Setup cost | Stateful analyzers accumulating across compilation |

**IOperation advantage**: semantics are baked into the operation tree — no callback to semantic model needed. Also language-agnostic (C# + VB).
> [Meziantou — IOperation analyzers](https://www.meziantou.net/writing-a-language-agnostic-roslyn-analyzer-using-ioperation.htm)

**Stateful pattern caveat**: `RegisterCompilationEndAction` is NOT fired unless full solution analysis is enabled in VS. Users with default settings never see these diagnostics during editing — only during builds.
> [patriksvensson.se](https://patriksvensson.se/posts/2020/03/how-to-write-a-stateful-roslyn-analyzer/)

### Performance

| Operation | Relative cost |
|-----------|--------------|
| Syntax tree traversal | Low |
| `SemanticModel.GetSymbolInfo()` | Medium (triggers binding) |
| `SemanticModel.GetTypeInfo()` | Medium |
| `SemanticModel.AnalyzeDataFlow()` | High |
| Full `Compilation` access | Highest |

Measured impact: 160+ project solution built in 1:42 without analyzers, 8:00 with StyleCop + FxCop — **4.7x increase**.
> [Meziantou — build time analysis](https://www.meziantou.net/understanding-the-impact-of-roslyn-analyzers-on-the-build-time.htm)

**Guidance**: Target under ~10ms per file scan. Check syntax first, fall through to semantics only when needed. Use `ConfigureGeneratedCodeAnalysis(None)`. The existing RQL001 already follows this pattern.

### Testing

The official framework is `Microsoft.CodeAnalysis.Testing` (`CSharpAnalyzerTest<T>`, `CSharpCodeFixTest<T>`). It supports inline markup:

```csharp
// [|text|] — diagnostic expected on "text"
// {|RQL001:text|} — specific diagnostic ID
```

**TUnit integration gap**: The testing framework ships MSTest and xUnit verifiers only. TUnit would require custom verifier wrappers or direct use of `AnalyzerTest<T>` base classes.

**Reference assemblies**: Use `Basic.Reference.Assemblies.NetStandard20` NuGet for stable test compilations.

### Packaging for Same-Solution

```xml
<ProjectReference Include="..\RepoQL.Analyzers\RepoQL.Analyzers.csproj"
                  OutputItemType="Analyzer"
                  ReferenceOutputAssembly="false"
                  PrivateAssets="All" />
```

Already configured in `Directory.Build.props`. **Known limitation**: ProjectReference doesn't auto-load analyzer dependencies — analyzers must be self-contained (no external NuGet beyond compiler SDK).
> [dotnet/roslyn#79272](https://github.com/dotnet/roslyn/issues/79272)

### Common Pitfalls

| Pitfall | Detail |
|---------|--------|
| `GetSymbolInfo().Symbol` returns null | Normal for incomplete code. Always check `CandidateSymbols`. |
| `ITypeSymbol` comparison | Must use `.Equals()`, not `==`. Different compilation paths produce distinct objects. |
| `netstandard2.0` and NRTs | NRTs not available. Workaround: internal attribute stubs. |
| Overload resolution failure | `CandidateSymbols` may contain valid symbol with `CandidateReason.OverloadResolutionFailure`. |

> [dotnet/roslyn#23913](https://github.com/dotnet/roslyn/issues/23913), [johnkoerner.com](https://johnkoerner.com/csharp/working-with-types-in-your-analyzer/)

---

## Part 2: RepoQL Convention Enforcement

Codebase analysis identified these enforceable conventions. Ordered by value/effort ratio.

### High Value, Low Effort

| ID | What | Detection | False Positive Risk | Notes |
|----|------|-----------|-------------------|-------|
| RQL002 | `using FluentAssertions;` banned | Syntax (`UsingDirectiveSyntax`) | Very Low | Preventive — zero current violations. License constraint. Mirrors RQL001 pattern. |
| RQL003 | `new DuckDBConnection(...)` outside `DuckDbDataStore` | Semantic (`ObjectCreationExpression`) | Very Low | Only 6 `new DuckDBConnection` calls exist — all in `DuckDbDataStore.cs` and tests. Catches the exact corruption vector. |
| RQL004 | `[ScalarUdf]`/`[StructuredUdf]` method with zero parameters | Syntax + Semantic | Very Low | Runtime error at `UdfRegistry.cs:139-143`. Compile-time catch with code fix (add dummy param with `[UdfDefault("''")]`). |

### Medium Value

| ID | What | Detection | False Positive Risk | Notes |
|----|------|-----------|-------------------|-------|
| RQL005 | Missing `[UdfClass]` on class containing UDF methods | Semantic | Low | 23 UDF classes, all consistent. Silent failure mode — UDF never registers. |
| RQL006 | Missing `[CommandClass]` on class containing `[Command]` methods | Semantic | Low | 7 command classes, all consistent. Same silent failure pattern. |
| RQL007 | `[Command]` method with wrong return type | Semantic | Low | Must return `CommandResult` or `Task<CommandResult>`. Runtime check at `CommandRegistry.cs:162-169`. |

### Lower Value or High False Positive Risk

| ID | What | Issue |
|----|------|-------|
| Broad DuckDB type ban | Flag all `DuckDBConnection` usage outside DuckDB project | Medium-high false positives — legitimate callback patterns in `GitHistoryIndexer`, `SnapshotLoader` |
| Class docs Purpose/Complexity | Check XML docs for keywords | High false positives — many small types legitimately lack this template |

**Observation**: The single-writer constraint is already architecturally enforced. `DuckDbDataStore` holds all connections; other code only receives connections through `WriteTransaction` callbacks. RQL003 adds defense-in-depth against the most dangerous violation (new connection creation).

---

## Part 3: Beyond Convention Enforcement

### Source Generators for Auto-Discovery

RepoQL uses runtime reflection for both UDF and command discovery:

```csharp
// UdfRegistry.cs:67-77 — AppDomain.CurrentDomain.GetAssemblies() scan
// CommandRegistry — same pattern
// Both annotated [RequiresUnreferencedCode]
```

An incremental source generator using `ForAttributeWithMetadataName` could:
- Generate registration code at compile time, eliminating reflection
- Validate UDF signatures match DuckDB type constraints
- Generate SQL macro stubs from attribute metadata
- Generate `help://` documentation from XML doc comments

This shifts discovery from runtime to compile time and improves AOT compatibility.
> [Thinktecture — ForAttributeWithMetadataName](https://www.thinktecture.com/en/net-core/roslyn-source-generators-high-level-api-forattributewithmetadataname/), [Roslyn incremental generators cookbook](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.cookbook.md)

### Diagnostic Suppressors

Rather than only adding warnings, a `DiagnosticSuppressor` understanding RepoQL patterns could silence false positives from standard analyzers:

- **CA1812** ("internal class never instantiated") on `[UdfClass]` and `[CommandClass]` types — they're discovered via reflection
- **CS0649** ("field never assigned") on DI-injected fields

This may deliver more developer happiness per line of code than new warnings.
> [Roslyn DiagnosticSuppressor design](https://github.com/dotnet/roslyn/blob/main/docs/analyzers/DiagnosticSuppressorDesign.md)

### SARIF Closed Loop

MSBuild supports SARIF output from Roslyn analyzers (`/p:ErrorLog=diagnostics.sarif`). RepoQL already imports SARIF as annotations. Analyzer diagnostics map directly to the `annotation` table (`kind='lint'`, `severity`, `rule_id`, `message`).

Every `dotnet build` could automatically produce structured analysis data queryable via: `SELECT * FROM annotation WHERE rule_id = 'RQL003'`.
> [Meziantou — SARIF output](https://www.meziantou.net/how-to-output-a-sarif-file-from-a-dotnet-project.htm)

### Agent-Optimized Diagnostics

CLAUDE.md states "Errors are actionable" and "Agents can self-heal." Diagnostic messages can be written for LLM consumption:

```
// Traditional:
RQL001: Use TUnit instead of xUnit

// Agent-oriented:
RQL001: Replace [Fact] with [Test] (TUnit). Replace [InlineData] with [Arguments].
Replace 'using Xunit;' with 'using TUnit;'. See help://testing for migration guide.
```

The existing RQL001 is already close to this pattern with its message format `'{0}' is an xUnit attribute — use {1} (TUnit) instead`.

### Architecture Test Libraries (Complement, Not Replacement)

Roslyn analyzers see one compilation at a time. Cross-project constraints require different tools:

| Tool | Mechanism | Cross-project |
|------|-----------|--------------|
| Roslyn Analyzer | Compiler integration | No — one project at a time |
| ArchUnitNET | Assembly/IL reflection in tests | Yes — whole solution |
| NetArchTest | Assembly/IL with fluent API | Yes — whole solution |
| NsDepCop | Roslyn analyzer with XML config | Namespace rules only |

For "ALL DuckDB writes through `DuckDbDataStore`" and "transport parity" — architecture test libraries provide what analyzers structurally cannot.
> [ArchUnitNET](https://github.com/TNG/ArchUnitNET), [NetArchTest](https://github.com/BenMorris/NetArchTest)

### Existing Analyzer Packages Worth Studying

| Package | Coverage | Rules |
|---------|----------|-------|
| Microsoft.CodeAnalysis.NetAnalyzers | Security, performance, design | CA-prefixed, default in .NET 5+ |
| Roslynator | 500+ rules, code simplification | RCS-prefixed |
| Meziantou.Analyzer | 170+ rules, async, string comparison | MA-prefixed |
| ErrorProne.NET | Correctness bugs other analyzers miss | EPC/ERP-prefixed |
| DapperAOT | Compile-time SQL validation | Dapper-specific |

---

## What Analyzers Cannot Enforce

Understanding the boundary prevents over-investment.

| Constraint | Why not analyzable |
|------------|-------------------|
| Cross-project invariants (single writer, transport parity) | Analyzers see one compilation at a time |
| Runtime behavior (error isolation, error message quality) | Static analysis cannot observe execution |
| String content semantics (SQL, URIs, Liquid templates) | Analyzers see strings, not structured content |
| Documentation quality | Can check existence, not usefulness |
| Temporal behavior (epoch ordering, idle processing timing) | Invisible to static analysis |

---

## Maintenance Considerations

| Concern | Detail |
|---------|--------|
| SDK version coupling | Analyzers can break on VS/SDK updates. Supporting multiple versions requires multi-targeting. |
| Performance budget | 3 analyzers are fine, 30 can be crippling. Profile cumulative impact. |
| False positive fatigue | The primary adoption killer. Prefer high-precision rules over broad coverage. |
| Test framework gap | TUnit verifiers don't exist — custom adapter work needed. |
| `dotnet watch` interaction | No documentation on analyzer behavior during hot reload. |

> [Andrew Lock — supporting multiple SDK versions](https://andrewlock.net/supporting-multiple-sdk-versions-in-analyzers-and-source-generators/)

---

## Gaps

| Topic | What couldn't be determined |
|-------|---------------------------|
| TUnit + `Microsoft.CodeAnalysis.Testing` | No documented integration. Unclear effort to build custom verifiers. |
| Analyzer load-time overhead | No published benchmarks on the cost of loading (vs running) analyzers. |
| IOperation completeness | Degree of coverage for C#-specific patterns not fully documented. |
| DuckDB-specific SQL validation | No existing Roslyn analyzer for DuckDB SQL dialect. Would require custom parser. |
| `dotnet watch` + analyzers | No documentation on interaction between hot reload and analyzer execution. |

---

## Sources

### Microsoft / Official
- [How to write a Roslyn Analyzer — .NET Blog](https://devblogs.microsoft.com/dotnet/how-to-write-a-roslyn-analyzer/)
- [Analyzer tutorial — Microsoft Learn](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/tutorials/how-to-write-csharp-analyzer-code-fix)
- [Analyzer Actions Semantics — dotnet/roslyn](https://github.com/dotnet/roslyn/blob/main/docs/analyzers/Analyzer%20Actions%20Semantics.md)
- [Incremental generators — dotnet/roslyn](https://github.com/dotnet/roslyn/blob/main/docs/features/incremental-generators.md)
- [Source generator cookbook — dotnet/roslyn](https://github.com/dotnet/roslyn/blob/main/docs/features/source-generators.cookbook.md)
- [DiagnosticSuppressor design — dotnet/roslyn](https://github.com/dotnet/roslyn/blob/main/docs/analyzers/DiagnosticSuppressorDesign.md)
- [Roslyn-SDK testing README](https://github.com/dotnet/roslyn-sdk/blob/main/src/Microsoft.CodeAnalysis.Testing/README.md)
- [Durable Functions Roslyn Analyzer — Microsoft Learn](https://learn.microsoft.com/en-us/azure/azure-functions/durable/durable-functions-roslyn-analyzer)

### Community (well-regarded, independently verifiable)
- [Meziantou — build time impact](https://www.meziantou.net/understanding-the-impact-of-roslyn-analyzers-on-the-build-time.htm) (2021)
- [Meziantou — IOperation analyzers](https://www.meziantou.net/writing-a-language-agnostic-roslyn-analyzer-using-ioperation.htm)
- [Meziantou — SARIF output](https://www.meziantou.net/how-to-output-a-sarif-file-from-a-dotnet-project.htm)
- [Patrik Svensson — stateful analyzers](https://patriksvensson.se/posts/2020/03/how-to-write-a-stateful-roslyn-analyzer/) (2020)
- [Andrew Lock — incremental generator pitfalls](https://andrewlock.net/creating-a-source-generator-part-9-avoiding-performance-pitfalls-in-incremental-generators/) (2022-2023)
- [Andrew Lock — multi-SDK support](https://andrewlock.net/supporting-multiple-sdk-versions-in-analyzers-and-source-generators/)
- [Thinktecture — ForAttributeWithMetadataName](https://www.thinktecture.com/en/net-core/roslyn-source-generators-high-level-api-forattributewithmetadataname/)
- [Viktor Ponamarev — Durable Functions analyzer lessons](https://medium.com/@vikpoca/how-i-built-a-roslyn-analyzer-to-save-developers-from-azure-durable-functions-bugs-ab2da8a9cc49)
- [Code Review Copilot with Roslyn](https://developersvoice.com/blog/ai-development/building-hybrid-ai-code-reviewer-with-roslyn/)

### Ecosystem
- [ArchUnitNET](https://github.com/TNG/ArchUnitNET)
- [NetArchTest](https://github.com/BenMorris/NetArchTest)
- [Apex.Analyzers](https://github.com/dbolin/Apex.Analyzers)
- [ErrorProne.NET](https://github.com/SergeyTeplyakov/ErrorProne.NET)
- [Excubo DI Validation](https://github.com/excubo-ag/Analyzers.DependencyInjectionValidation)
- [NsDepCop](https://github.com/realvizu/NsDepCop)
- [DapperAOT](https://github.com/DapperLib/DapperAOT)
- [awesome-analyzers](https://github.com/cybermaxs/awesome-analyzers)
- [csharp-source-generators](https://github.com/amis92/csharp-source-generators)
