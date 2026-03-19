---
description: Research on TUnit testing framework patterns for local development and GitHub Actions CI in RepoQL
tags: [tunit, testing, ci, github-actions, awesomeassertions, fakeiteasy]
audience: { human: 50, agent: 50 }
purpose: { research: 85, reference: 15 }
---

# TUnit Effective Usage Research

Research for improving testing practices and CI pipeline efficiency in RepoQL.

*Research date: 2026-03-18*

## Context

RepoQL uses TUnit 1.12.93, AwesomeAssertions 9.2.1, and FakeItEasy across 28 test projects containing 1,962 tests. Tests run locally via `dotnet run` and in GitHub Actions via a per-project loop. This research surveys TUnit's capabilities, identifies what RepoQL uses today, what it doesn't use yet, and how the CI pipeline works. It informs decisions about testing patterns, CI optimization, and developer experience.

**In scope:** TUnit framework features, local dev workflow, GitHub Actions CI, AwesomeAssertions integration, parallelism, filtering, reporting, data-driven testing, lifecycle hooks.

**Out of scope:** Alternative frameworks (we're committed to TUnit), E2E/Playwright testing (not in use), TUnit.Mocks (requires C# 14).

---

## TUnit Architecture

TUnit uses C# source generators for compile-time test discovery, eliminating runtime reflection entirely. Tests are registered as metadata during compilation, catching many errors at build time. This is the core differentiator from xUnit and NUnit.

> [TUnit GitHub](https://github.com/thomhurst/TUnit) — 3,803 stars, MIT license
> [Tom Longhurst, "Why I Spent 2 Years Building TUnit"](https://medium.com/@thomhurst/tunit-why-i-spent-2-years-building-a-new-net-testing-framework-86efaec0b8b8) — creator's motivation

Key architectural choices:

| Aspect | TUnit | xUnit | NUnit |
|--------|-------|-------|-------|
| Discovery | Source generators (compile-time) | Reflection (runtime) | Reflection (runtime) |
| Platform | Microsoft.Testing.Platform | VSTest | VSTest |
| Test attribute | `[Test]` for everything | `[Fact]`/`[Theory]` split | `[Test]`/`[TestCase]` |
| Instance model | New instance per test (no opt-out) | New instance per test | Shared instance per class |
| Parallelism | All tests parallel by default | Class-level parallel | Configurable |
| Async | Native async-first throughout | Requires `IAsyncLifetime` | Native |
| AOT | Supported | Not supported | Not supported |
| Packages needed | 1 (`TUnit`) | Multiple | Multiple |

**Execution model:** Test projects are executable programs (`OutputType=Exe`). `dotnet run` is the preferred execution method; `dotnet test` works but requires `--` separator for TUnit flags.

> [TUnit Running Your Tests](https://tunit.dev/docs/getting-started/running-your-tests/)

**Engine modes:** Source Generation (default), Reflection (via `--reflection` flag, needed for F#/VB.NET), and Hybrid (source gen with reflection fallback).

> [TUnit Engine Modes](https://tunit.dev/docs/execution/engine-modes/)

---

## Test Lifecycle

TUnit uses `[Before]` and `[After]` attributes with scope parameters:

| Scope | Attribute | Context parameter | Static? |
|-------|-----------|-------------------|---------|
| TestSession | `[Before(TestSession)]` | `TestSessionContext` | Yes |
| Assembly | `[Before(Assembly)]` | `AssemblyHookContext` | Yes |
| Class | `[Before(Class)]` | `ClassHookContext` | Yes |
| Test | `[Before(Test)]` | `TestContext` | No |

Global hooks via `[BeforeEvery(Test)]` and `[AfterEvery(Test)]` apply cross-cutting concerns across all tests. Must be static methods in a static class.

After hooks receive a `CancellationToken` and can inspect `context.Execution.Result?.State` for conditional cleanup (e.g., screenshot on failure).

> [TUnit Test Lifecycle](https://tunit.dev/docs/test-lifecycle/setup/)
> [TUnit Migration from xUnit](https://tunit.dev/docs/migration/xunit/)

**Gotcha:** Constructor runs even for skipped tests, but `DisposeAsync` does NOT run — potential resource leak if constructors allocate expensive resources for tests that may be skipped.

> [TUnit Troubleshooting](https://tunit.dev/docs/troubleshooting/)

---

## Parallelism

All tests run in parallel by default — more aggressive than xUnit (class-level) or NUnit (configurable).

**Key distinction from xUnit:** xUnit limits thread count, but a single thread can run multiple async tests when they yield. TUnit limits actual concurrent test count.

> [TUnit Framework Differences](https://tunit.dev/docs/comparison/framework-differences/)

Control mechanisms:

| Mechanism | Scope | Behavior |
|-----------|-------|----------|
| `--maximum-parallel-tests N` | Global CLI | Hard cap on concurrent tests |
| `[NotInParallel("key")]` | Method/class | Serializes tests with same key; different keys still parallel |
| `[assembly: NotInParallel]` | Assembly | Disables parallelism for entire project |
| `[ParallelLimiter<T>]` | Method/class/assembly | Shared concurrency cap via `IParallelLimit` implementation |
| `[DependsOn(nameof(Other))]` | Method | Waits for dependency without disabling parallelism globally |

The constraint key on `[NotInParallel]` is more powerful than NUnit's binary on/off. Tests with the same key serialize with each other but still run in parallel with tests having different keys.

> [TUnit Not In Parallel](https://thomhurst.github.io/TUnit/docs/tutorial-extras/not-in-parallel/)
> [TUnit Parallel Limiter](https://thomhurst.github.io/TUnit/docs/tutorial-extras/parallel-limiter/)

**Caveat:** If a test uses `[DependsOn]` and the depended-on test has a different parallel limit, the parallel limit is not guaranteed to be honored.

---

## Data-Driven Testing

| Mechanism | Purpose | Example |
|-----------|---------|---------|
| `[Arguments(...)]` | Inline data on test method | `[Arguments(1, 1, 2)]` |
| `[MethodDataSource(nameof(M))]` | Static method returning `IEnumerable<T>` | Supports tuples |
| `[ClassDataSource<T>]` | Inject class instance with lifecycle | `SharedType.PerTestSession`, `PerClass`, etc. |
| `[MatrixDataSource]` | All combinations of `[Matrix]` parameters | 3 params x 10 values = 1,000 tests |
| `[MatrixExclusion]` | Exclude specific matrix combinations | Prevents combinatorial explosion |

`[ClassDataSource<T>]` supports up to 5 generic type arguments and property injection with dependency graphs. TUnit detects properties marked with data source attributes, builds a dependency graph, initializes in correct order, and disposes in reverse order.

> [TUnit Data Driven Tests](https://tunit.dev/docs/test-authoring/arguments/)
> [TUnit Matrix Tests](https://tunit.dev/docs/test-authoring/matrix-tests/)
> [TUnit Property Injection](https://tunit.dev/docs/test-lifecycle/property-injection/)
> [TUnit Nested Data Sources](https://tunit.dev/docs/test-authoring/nested-data-sources/)

**AOT gotcha:** `[MethodDataSource(typeof(DataClass))]` (non-generic form) causes "source generator did not generate" errors. Use the generic form `[MethodDataSource<DataClass>]` instead.

> [TUnit AOT Compatibility](https://tunit.dev/docs/test-authoring/aot-compatibility/)

---

## Test Filtering

TUnit uses **tree-node filter syntax** (from Microsoft.Testing.Platform), NOT VSTest filter syntax.

Path structure: `/<TestSession>/<Assembly>/<Class>/<Method>[properties]`

| Pattern | Effect |
|---------|--------|
| `/*/*/MyTestClass/*` | All tests in a class |
| `/*/*/MyTestClass/MyTestMethod` | Specific test method |
| `/*/*/*/*[Category=Integration]` | By category property |
| `/*/*/*/*[Category=Smoke]&[Priority=High]` | Multiple conditions (AND) |
| `/*/*/ClassA/*\|/*/*/ClassB/*` | Multiple patterns (OR) |
| `/*/*/*/*[Category!=Performance]` | Exclusion |

Usage:
```bash
# dotnet run (preferred)
dotnet run -- --treenode-filter "/*/*/MyTestClass/*"

# dotnet test (requires -- separator)
dotnet test -- --treenode-filter "/*/*/MyTestClass/*"
```

> [TUnit Test Filters](https://tunit.dev/docs/execution/test-filters/)

---

## Assertions and AwesomeAssertions

TUnit ships built-in async-first assertions (`await Assert.That(x).IsEqualTo(y)`), but RepoQL uses AwesomeAssertions instead, which provides the `.Should()` fluent syntax identical to FluentAssertions.

AwesomeAssertions explicitly lists TUnit as a supported framework. If auto-detection fails, set the framework explicitly via app.config with `tunit` as the value.

> [AwesomeAssertions Introduction](https://awesomeassertions.org/introduction)

**Namespace clash prevention:** TUnit adds implicit global usings that can conflict with AwesomeAssertions. Disable with `<TUnitImplicitUsings>false</TUnitImplicitUsings>` and `<TUnitAssertionsImplicitUsings>false</TUnitAssertionsImplicitUsings>` in csproj. RepoQL does not set these overrides; no clash has been reported, suggesting the current implicit usings are compatible.

> [TUnit Migration from xUnit](https://tunit.dev/docs/migration/xunit/) — mentions implicit using settings

TUnit also supports custom source-generated assertions via `[GenerateAssertion]` attribute, enabling async operations inside assertions (e.g., database lookups).

> [TUnit Source Generator Assertions](https://tunit.dev/docs/assertions/extensibility/source-generator-assertions/)

---

## Output and Reporting

**Logging architecture:** Sink-based. All output routes through registered log sinks. `Console.WriteLine()` is intercepted and correlated to the test via async context.

Output methods:
- `Console.WriteLine()` — auto-captured
- `TestContext.Current.GetDefaultLogger()` — logger instance
- `TestContext.Current!.Output.WriteLine()` — standard output

**Output modes:**
- `Normal` (default for console) — minimal
- `Detailed` (default for IDE, or via `--output Detailed`) — full formatted output with test names and duration

**TRX reports:** Via `Microsoft.Testing.Extensions.TrxReport` (included with TUnit):
```bash
dotnet run -- --report-trx --report-trx-filename results.trx --results-directory ./reports
```

**GitHub Actions reporter:** TUnit auto-detects `GITHUB_ACTIONS` environment variable and generates a workflow summary. Two styles controlled by `--github-reporter-style`:
- `collapsible` (default since v1.0.0) — counts passed, expands only failures
- `full` — expands everything

Can be disabled with `TUNIT_DISABLE_GITHUB_REPORTER=true`.

> [TUnit CI/CD Reporting](https://tunit.dev/docs/execution/ci-cd-reporting/)
> [TUnit Logging](https://tunit.dev/docs/customization-extensibility/logging/)

**OpenTelemetry integration (March 2026):** TUnit now captures OpenTelemetry activity spans and embeds them as trace timelines in HTML test reports via `TUnit.AspNetCore`.

> [Tom Longhurst, "TUnit Now Captures OpenTelemetry Traces"](https://medium.com/@thomhurst/tunit-now-captures-opentelemetry-traces-in-test-reports-cf0ed728fae4)

---

## Retry and Timeout

`[Retry(N)]` retries on any exception. Each retry gets a fresh timeout. Custom retry logic via subclassing `RetryAttribute` and overriding `ShouldRetry`.

`[Timeout(milliseconds)]` at test, class, or assembly level. Test-level takes priority.

> [TUnit Timeouts](https://tunit.dev/docs/execution/timeouts/)

**Known issue:** Hooks in `IAsyncInitializer.InitializeAsync()` have no timeout protection. If `InitializeAsync` throws, tests may hang indefinitely. Issue #4789 deferred to v2.

> [TUnit Issue #4789](https://github.com/thomhurst/TUnit/issues/4789)
> [TUnit Issue #4715](https://github.com/thomhurst/TUnit/issues/4715)

---

## Configuration

**`testconfig.json`:** Key-value configuration within test projects. Values retrieved via `TestContext.Configuration.Get(key)`.

**Command-line flags (selected):**

| Flag | Purpose |
|------|---------|
| `--maximum-parallel-tests N` | Cap concurrent tests |
| `--timeout [h\|m\|s]` | Global execution timeout |
| `--output Detailed` | Verbose output |
| `--treenode-filter "..."` | Test filtering |
| `--report-trx` | Generate TRX report |
| `--results-directory path` | Output directory |
| `--diagnostic-verbosity level` | Logging verbosity |
| `--github-reporter-style style` | GitHub Actions output format |
| `--fail-fast` | Cancel remaining tests after first failure |
| `--list-tests` | List all tests without running |
| `--coverage` | Enable code coverage |

> [TUnit Command Line Flags](https://tunit.dev/docs/reference/command-line-flags/)

**Environment variables:**

| Variable | Purpose |
|----------|---------|
| `TUNIT_EXECUTION_MODE` | Switch between source gen and reflection |
| `TUNIT_ENABLE_IDE_STREAMING` | Real-time output for IDEs |
| `TUNIT_DISABLE_LOGO` | Suppress TUnit banner |
| `TUNIT_DISABLE_GITHUB_REPORTER` | Disable GitHub Actions auto-reporting |
| `TUNIT_GITHUB_REPORTER_STYLE` | Set reporter style without CLI flag |

---

## Community Ecosystem

| Package | Purpose |
|---------|---------|
| AutoFixture.TUnit | Auto-generated test data (`[AutoDataSource]`, `[AutoArguments]`) |
| Reqnroll.TUnit | BDD/Cucumber-style integration |
| Verify.TUnit | Snapshot testing with auto parameter detection |
| TUnit.AspNetCore | Per-test isolation for ASP.NET Core integration tests |
| TUnit.Playwright | Browser testing integration |
| TUnit.Mocks (beta) | Source-generated AOT-compatible mocking (requires C# 14) |

> [AutoFixture.TUnit](https://github.com/AutoFixture/AutoFixture.TUnit)
> [Reqnroll TUnit](https://docs.reqnroll.net/latest/integrations/tunit.html)
> [Verify.TUnit](https://www.nuget.org/packages/Verify.TUnit/)

---

## RepoQL Current State

### Test Infrastructure

28 test projects under `src/tests/`, plus `RepoQL.Testing` (shared library, not a test project). All target `net10.0`, `OutputType=Exe`.

**Shared infrastructure in `RepoQL.Testing`:**

| Component | Purpose |
|-----------|---------|
| `FormatIntegrationTestBase` | Base class for format tests; provides `CreateLogger<T>()`, `CreateTestItem()`, `CreateHarness()` |
| `FormatTestHarness` | Fluent builder for format processing pipeline |
| `FormatTestResult` + `FormatTestResultAssertions` | Custom AwesomeAssertions class for format test output |
| `DuckDbTestStore` | In-memory DuckDB with RepoQL schema |
| `GraphAssertionHarness` | Assertions for graph database state |
| `IndexedRepoBuilder` | Full in-memory repo builder (657 lines) |
| `IndexingAssertionExtensions` | Pipeline and catalog invocation assertions |
| `TestLoggerFactory` | Routes `Microsoft.Extensions.Logging` to TUnit logger |

**Build infrastructure:**

| File | Effect |
|------|--------|
| `Directory.Build.props` | Auto-applies warnings, compiles shared `TUnitOpenTelemetrySetup.cs` into every test assembly, adds OpenTelemetry packages |
| `build/TestDependencies.props` | Auto-adds `CodeCoverage` and `TrxReport` packages to all `*.Tests` projects |
| `build/MakeInternalsVisibleToTests.targets` | Auto-generates `InternalsVisibleTo` for `.Tests` and `.ApiTests` assemblies |
| `RepoQL.Analyzers` (RQL007) | Roslyn analyzer warning on `BindingFlags.NonPublic` reflection in test files |

### Attribute Usage

| Attribute | Count | Adoption |
|-----------|-------|----------|
| `[Test]` | 1,962 across 208 files | Universal |
| `[DisplayName]` | 547 across 38 files | 18% of test files |
| `[Arguments]` | 188 across 17 files | Common for parameterized |
| `[NotInParallel]` | 8 classes | DuckDB, dashboard, diagnostics, host state |
| `[Timeout]` | 6 instances | Integration/pipeline tests (60-180s) |
| `[Skip]` | 3 instances | Known limitations |
| `[Before(Test)]` | 3 classes | Config loader tests, MCP config |
| `[Before(TestSession)]` | 2 classes | `TUnitOpenTelemetrySetup` (shared), `GlobalSetup` (CLI) |
| `[assembly: Category("Unit")]` | 6 projects | Only format loaders + Rendering |

**Not used:** `[MethodDataSource]`, `[ClassDataSource]`, `[MatrixDataSource]`, `[Retry]`, `[Explicit]`, `[ParallelLimiter]`, `[DependsOn]`.

### Assertion and Mocking Patterns

AwesomeAssertions: `using AwesomeAssertions;` in 200 files, ~5,742 `.Should()` calls. Zero FluentAssertions usage.

FakeItEasy: 22 test files, 186 `A.CallTo` calls, 50 `MustHaveHappened` verifications. Many tests use concrete test doubles instead: `DuckDbTestStore`, `IndexedRepoBuilder`, `FormatTestHarness`, stub classes.

`because` parameter: Used in only 3 of 200+ files despite being highlighted in the testing guidelines.

### Parallelism in Practice

All 8 `[NotInParallel]` usages use `nameof(ClassName)` as the constraint key. No `[ParallelLimiter]` or global parallelism configuration beyond TUnit defaults.

TUnit's `CancellationToken` injection: Only used in `IndexingFullPipelineTests` (3 methods). Most async tests pass `CancellationToken.None` directly.

### GitHub Actions CI

Single workflow at `.github/workflows/ci-tests.yml`:

| Aspect | Configuration |
|--------|---------------|
| Runner | `ubuntu-latest`, 30-minute timeout |
| SDK | `dotnet-version: '10.0.1xx'` |
| Build | `dotnet build RepoQL.sln --configuration Release` |
| Test execution | Loop over `*.Tests.csproj` from solution, each via `dotnet run --project <proj> --configuration Release --no-build -- --coverage --coverage-output-format cobertura --report-trx` |
| Coverage | Cobertura format, merged via `dotnet-coverage merge`, report via `ReportGenerator-GitHub-Action@5` |
| Test results | TRX format, published via `EnricoMi/publish-unit-test-result-action@v2` |
| Caching | `actions/cache@v4` on `~/.nuget/packages`, keyed on `hashFiles('**/*.csproj')` |
| Concurrency | `cancel-in-progress: true` per workflow+ref |
| Disk space | Pre-cleanup step (android, haskell, large packages, docker, swap) |

The workflow does not use TUnit's built-in GitHub Actions reporter (which auto-generates job summaries when `GITHUB_ACTIONS` is detected). This is likely already active since the env var is set automatically by GitHub Actions, producing a summary alongside the EnricoMi TRX-based results.

---

## `dotnet run` vs `dotnet test` in CI

| Aspect | `dotnet run` | `dotnet test` |
|--------|-------------|---------------|
| Flag passing | Direct: `dotnet run -- --report-trx` | Requires `--`: `dotnet test -- --report-trx` |
| Solution-level | Cannot target .sln; requires project loop | Auto-discovers all test projects in solution |
| TUnit docs preference | "Preferred method" | Standard and broadly compatible |
| `--no-build` | Supported | Supported |
| Filter syntax | Identical (`--treenode-filter`) | Identical |
| CI pattern | Explicit per-project control | Simpler workflow but less control |

RepoQL uses `dotnet run` in a per-project loop, which is the more explicit approach. `dotnet test` on the solution would simplify the workflow but lose per-project control over failure handling and output isolation.

> [TUnit Running Your Tests](https://tunit.dev/docs/getting-started/running-your-tests/)
> [TUnit CI/CD Pipelines](https://tunit.dev/docs/examples/tunit-ci-pipeline/)

---

## CI Reporting Options

| Tool | Mechanism | RepoQL usage |
|------|-----------|-------------|
| TUnit built-in GitHub reporter | Auto-detects `GITHUB_ACTIONS`, generates job summary | Likely active (env var auto-set) |
| `EnricoMi/publish-unit-test-result-action@v2` | PR comments with test delta detection from TRX | In use |
| `dorny/test-reporter` | Creates separate check runs with annotations | Not in use |
| `danielpalme/ReportGenerator-GitHub-Action@5` | Coverage HTML + Markdown from Cobertura | In use |
| GitHubActionsTestLogger (NuGet) | Publishes directly to Job Summary API | Not in use |

> [EnricoMi publish-unit-test-result-action](https://github.com/marketplace/actions/publish-test-results)
> [TUnit CI/CD Reporting](https://tunit.dev/docs/execution/ci-cd-reporting/)

**Note on GitHubActionsTestLogger:** When using Microsoft.Testing.Platform (TUnit's platform), do NOT mark this package as `PrivateAssets="all"`.

---

## Test Sharding

.NET does not provide native test sharding. Manual implementation via GitHub Actions matrix, where each job runs a subset using `--treenode-filter` or project-level splitting. Post-sharding: a merge job collects TRX/coverage artifacts for combined reporting.

> [Optimizing .NET Test Runs with Sharding](https://gor-grigoryan.medium.com/optimizing-net-test-runs-in-github-actions-with-test-sharding-for-faster-ci-cd-315e610cf560)

RepoQL's per-project loop is already a natural sharding boundary. Matrix-ifying the project list across runners would parallelize CI execution without changing test code.

---

## Known Issues and Gotchas

| Issue | Impact | Source |
|-------|--------|--------|
| Hooks in `InitializeAsync` can hang indefinitely | Test hangs with no timeout protection | [Issue #4789](https://github.com/thomhurst/TUnit/issues/4789), deferred to v2 |
| Constructor runs for skipped tests, `DisposeAsync` does not | Resource leaks possible | [TUnit Troubleshooting](https://tunit.dev/docs/troubleshooting/) |
| Cross-assembly hooks may not execute | Hooks in referenced class libraries may be silently skipped | [TUnit Troubleshooting](https://tunit.dev/docs/troubleshooting/) |
| No Coverlet support | Must use `Microsoft.Testing.Extensions.CodeCoverage` instead | [TUnit Code Coverage](https://tunit.dev/docs/extending/code-coverage/) |
| AOT-incompatible data sources | Non-generic `[MethodDataSource(typeof(T))]` fails; use `[MethodDataSource<T>]` | [TUnit AOT Compatibility](https://tunit.dev/docs/test-authoring/aot-compatibility/) |
| AI tools hallucinate more with TUnit | Less training data than xUnit/NUnit; AI may generate incorrect TUnit code | [Panu Oksala comparison](https://oksala.net/2025/10/27/unit-testing-with-tunit/) |
| `WebApplicationFactory` not thread-safe | Singleton services may be created multiple times under parallelism | [Issue #1647](https://github.com/thomhurst/TUnit/issues/1647) |
| `[DependsOn]` + different `[ParallelLimiter]` | Parallel limit not guaranteed when dependency has different limiter | [TUnit docs](https://tunit.dev/docs/comparison/framework-differences/) |

---

## RepoQL-Specific Observations

### Inconsistencies observed

| Area | Observation |
|------|-------------|
| `GlobalSetup.cs` | Only 8 of 28 test projects have one; only 6 declare `[assembly: Category("Unit")]` |
| `global using AwesomeAssertions` | Only `RepoQL.Protocol.Tests` uses it; all other 200 files have per-file `using` |
| `[DisplayName]` | Used in 38 of 208 test files (18%) despite guidelines emphasizing it |
| `because` parameter | Used in 3 of 200+ files despite guidelines highlighting it |
| `UseTestingPlatform` | `RepoQL.Tests` explicitly sets it; others rely on `directory.build.props` implicit |
| `CancellationToken` injection | Only 1 test class uses TUnit's built-in token; most pass `CancellationToken.None` |
| Stale `.bak` file | `src/tests/RepoQL.Tests/AnnotationsTests.cs.bak` exists with duplicate usings |

### Unused TUnit features

| Feature | Potential applicability |
|---------|----------------------|
| `[MethodDataSource]` | Test data that needs computation or external loading |
| `[ClassDataSource<T>]` | Shared expensive infrastructure (e.g., `DuckDbTestStore` per-session instead of per-test) |
| `[MatrixDataSource]` | Format loader cross-product testing |
| `[Retry]` | Flaky integration tests or tests with external dependencies |
| `[ParallelLimiter<T>]` | Fine-grained concurrency control beyond `[NotInParallel]` |
| `[DependsOn]` | Tests that require ordering without disabling all parallelism |
| `CancellationToken` injection | Built-in test cancellation token vs manual `CancellationToken.None` |
| `--fail-fast` | PR validation builds for faster feedback on failure |
| `testconfig.json` | Externalized test configuration values |

### Reflection violations

Despite the `NoReflectionInTestsAnalyzer` (RQL007):
- `IndexingTestItemExtensions.cs` — uses `GetMethod("SetEpoch", BindingFlags.NonPublic)`. `SetEpoch` should likely be `internal`.
- `DuckDbDataStoreTests.cs:2137` — reflects into `DuckDBException` constructor (external library, cannot change visibility).

---

## Gaps

- **No independent benchmarks found** — TUnit's performance claims are architecture-based (source gen vs reflection), not empirically demonstrated by third parties. The official benchmark page exists at `tunit.dev/docs/benchmarks/` but specific comparison numbers were not retrievable.
- **`testconfig.json` schema** — found that it exists and supports key-value pairs via `TestContext.Configuration.Get(key)`, but no full schema reference or exhaustive list of supported keys.
- **`[TestExecutor<T>]`** — mentioned in search results as allowing custom test invocation control (e.g., threading requirements) but detailed documentation not retrieved.
- **Version churn** — TUnit has 200+ releases. RepoQL is on 1.12.93; latest appears to be in 1.18.x range. The rapid release cadence means RepoQL is several minor versions behind.
- **TUnit's built-in GitHub reporter behavior** — unclear whether it's already producing summaries in RepoQL's CI alongside the EnricoMi action, or if there's deduplication.

---

## Summary

| Dimension | Current state | TUnit capability |
|-----------|--------------|-----------------|
| Test count | 1,962 across 28 projects | Scales via source generators |
| Parallelism control | `[NotInParallel]` with class name keys | Also: `[ParallelLimiter<T>]`, `[DependsOn]`, `--maximum-parallel-tests`, constraint key groups |
| Data-driven tests | `[Arguments]` only (188 usages) | Also: `[MethodDataSource]`, `[ClassDataSource<T>]`, `[MatrixDataSource]`, nested data sources |
| Lifecycle hooks | `[Before(Test)]` (3 classes), `[Before(TestSession)]` (2 classes) | Also: `[BeforeEvery]`, Assembly/Class scopes, `IAsyncInitializer` |
| CI execution | Per-project `dotnet run` loop | `--fail-fast` for fast feedback, `--github-reporter-style` for summaries |
| Test reporting | TRX + EnricoMi action + ReportGenerator | Also: built-in GitHub reporter (auto-active), OpenTelemetry trace capture |
| Retry | Not used | `[Retry(N)]` with custom `ShouldRetry` override |
| Filtering | `--treenode-filter` by method name | Also: category filtering, property filtering, AND/OR combinators |
| Shared infrastructure | `DuckDbTestStore` created per-test | `[ClassDataSource<T>(Shared = SharedType.PerTestSession)]` for expensive resources |
| Assertions | AwesomeAssertions `.Should()` (5,742 calls) | Compatible; TUnit also offers source-generated custom assertions |

### Sources Consulted

| Source | What it establishes |
|--------|-------------------|
| [TUnit Official Docs](https://tunit.dev/) | Authoritative feature reference |
| [TUnit GitHub](https://github.com/thomhurst/TUnit) | Source, issues, discussions |
| [Tom Longhurst's Medium articles](https://medium.com/@thomhurst) | Creator's design philosophy and recent features |
| [Andrew Lock's migration post](https://andrewlock.net/converting-an-xunit-project-to-tunit/) | Real-world migration experience |
| [Hakan Fostok's migration post](https://medium.com/@hakam.fstk/why-i-switched-from-xunit-to-tunit-b4599cc2487a) | Migration motivations |
| [Panu Oksala's comparison](https://oksala.net/2025/10/27/unit-testing-with-tunit/) | Framework comparison including AI tooling caveat |
| [AwesomeAssertions docs](https://awesomeassertions.org/introduction) | Framework compatibility |
| RepoQL codebase (208 test files, CI workflow, build props) | Current patterns and conventions |
