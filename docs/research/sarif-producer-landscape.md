---
description: What real SARIF reports look like across major producers — structure, fields used, quirks, and interop hazards
tags: [sarif, snyk, sonarcloud, qodana, codeql, semgrep, roslyn, trivy, import, annotations]
audience: { human: 40, agent: 60 }
purpose: { research: 85, reference: 15 }
---

# SARIF Producer Landscape

> What do real-world SARIF files actually contain — and where do they deviate from each other and the spec?

This research informs the SARIF import design. It presents what each tool produces, not what RepoQL should do about it. Seven major producers were examined against the SARIF 2.1.0 specification.

---

## The Spec in Brief

SARIF 2.1.0 (OASIS Standard) defines a JSON format for static analysis results. The structure is minimal in what it requires, generous in what it allows.

**Required fields, per level:**

| Object | Required |
|--------|----------|
| `sarifLog` | `version` ("2.1.0"), `runs` |
| `run` | `tool` |
| `tool` | `driver` |
| `toolComponent` | `name` |
| `result` | `message` |
| `message` | `text` or `id` (at least one) |
| `reportingDescriptor` (rule) | `id` |
| `physicalLocation` | `artifactLocation` or `address` |
| `region` | `startLine` or `charOffset` or `byteOffset` |

Everything else is optional. `ruleId`, `level`, `locations`, `fingerprints` — all optional per spec. Producers vary wildly in what they populate.

**Coordinate system:** Lines are 1-based. `endColumn` is exclusive (points to the character *after* the end). Byte and character offsets are 0-based. `columnKind` disambiguates column measurement (`utf16CodeUnits` vs `unicodeCodePoints`) but most producers don't set it.

**Multi-run:** The `runs` array can hold results from multiple tools. Each run has its own `tool` object. In practice, most producers emit one run per file.

Sources: [SARIF v2.1.0 Specification (OASIS)](https://docs.oasis-open.org/sarif/sarif/v2.1.0/sarif-v2.1.0.html), [SARIF 2.1.0 JSON Schema](https://github.com/oasis-tcs/sarif-spec/blob/main/sarif-2.1/schema/sarif-schema-2.1.0.json)

---

## Producer Comparison

### Field Population Matrix

What each tool actually puts in its SARIF output:

| Field | Snyk Code | Snyk OSS | Qodana | CodeQL | Semgrep | ESLint | Roslyn | Trivy |
|-------|-----------|----------|--------|--------|---------|--------|--------|-------|
| `tool.driver.name` | SnykCode | Snyk Open Source | QDJVM/QDJS/etc | CodeQL command-line toolchain | Semgrep | ESLint | Microsoft (R) Visual C# Compiler | Trivy Vulnerability Scanner |
| `tool.driver.rules[]` | Yes | Yes | **Empty** (on extensions) | Yes | Yes | Yes | Yes | Yes |
| `tool.extensions[]` | No | No | **Yes** (all rules here) | No | No | No | No | No |
| `result.ruleId` | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| `result.level` | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| `result.message.text` | Yes | Yes | Yes | Yes | Yes | Yes | Yes | Yes |
| `result.message.markdown` | Yes | No | Yes | No | No | No | No | No |
| `locations[].physicalLocation` | Yes | Degenerate | Yes | Yes | Yes | Yes | Yes | Yes |
| `region.startLine` | Yes | 1 (always) | Yes | Yes | Yes | Yes | Yes | Yes |
| `region.endLine` | Yes | No | **No** | Yes | Yes | No | Yes | Yes |
| `region.startColumn` | Yes | No | Yes | Yes | Yes | Yes | Yes | 1 (always) |
| `region.endColumn` | Yes | No | **No** | Yes | Yes | No | Yes | 1 (always) |
| `region.charOffset` | No | No | Yes | Sometimes | No | No | No | No |
| `region.charLength` | No | No | Yes | Sometimes | No | No | No | No |
| `region.snippet` | No | No | Yes | Yes | Yes | No | No | No |
| `contextRegion` | No | No | Yes | Yes | No | No | No | No |
| `partialFingerprints` | No | No | Yes | Yes | No | No | No | No |
| `fingerprints` | Yes | No | No | No | Yes | No | No | No |
| `codeFlows` | Yes | No | No | Yes (path-problem) | No | No | No | No |
| `relatedLocations` | No | No | No | Yes | No | No | No | No |
| `fixes` | No | No | No | No | No | No | No | No |
| `suppressions` | Conditional | No | No | Yes | No | No | Yes | No |
| `baselineState` | No | No | Conditional | No | No | No | No | No |
| `uriBaseId` | %SRCROOT% | No | SRCROOT | Yes | Yes | No | No | ROOTPATH |
| `columnKind` | No | No | No | Yes | No | No | utf16CodeUnits | utf16CodeUnits |
| `logicalLocations` | No | No | Yes (module) | No | No | No | No | No |
| `rule.help.markdown` | Yes | Yes | No | No | No | No | No | No |
| `rule.helpUri` | No | No | No | Yes | No | Yes | Yes | No |
| `security-severity` | **Missing** | **Missing** | No | Numeric | **Text (bug)** | N/A | N/A | Numeric |

Sources: [snyk-labs/nodejs-goof](https://github.com/snyk-labs/nodejs-goof), [JetBrains/projector-server](https://github.com/JetBrains/projector-server/blob/master/qodana.sarif.json), [CodeQL CLI SARIF output - GitHub Docs](https://docs.github.com/en/code-security/codeql-cli/using-the-advanced-functionality-of-the-codeql-cli/sarif-output), [Semgrep JSON and SARIF fields](https://semgrep.dev/docs/semgrep-appsec-platform/json-and-sarif), [@microsoft/eslint-formatter-sarif](https://www.npmjs.com/package/@microsoft/eslint-formatter-sarif), [Roslyn Error Log Format](https://github.com/dotnet/roslyn/blob/main/docs/compilers/Error%20Log%20Format.md), [Trivy SARIF reports](https://deepwiki.com/aquasecurity/trivy/7.2-sarif-and-integration-reports)

---

## Snyk

### Products and Output

Snyk has four products, each producing a separate SARIF file with one run:

| Command | Driver name | Scan type |
|---------|-------------|-----------|
| `snyk code test --sarif` | SnykCode | SAST |
| `snyk test --sarif` | Snyk Open Source | SCA (dependency) |
| `snyk container test --sarif` | (separate) | Container |
| `snyk iac test --sarif` | (separate) | Infrastructure as Code |

### ruleId Formats

| Product | Format | Examples |
|---------|--------|----------|
| Code (SAST) | `language/RuleName` | `javascript/XSS`, `javascript/Sqli`, `javascript/NoHardcodedPasswords` |
| Open Source | `SNYK-LANG-PKG-NUMBER` | `SNYK-JS-ADMZIP-1065796`, `SNYK-JS-AJV-584908` |
| Open Source (legacy) | `npm:package:date` | `npm:adm-zip:20180415`, `npm:ejs:20161128` |
| IaC | `SNYK-CC-PROVIDER-NUMBER` | `SNYK-CC-TF-118`, `SNYK-CC-00172` |

Both modern and legacy rule ID formats appear in the same SARIF file.

### Severity Mapping

| Snyk Severity | SARIF `level` |
|---------------|---------------|
| Critical | `error` (not used in Code/SAST) |
| High | `error` |
| Medium | `warning` |
| Low | `note` |

Snyk does not populate the `security-severity` numeric property. This causes GitHub upload failures.

### File Paths

**Code (SAST):** Relative paths with `uriBaseId: "%SRCROOT%"`. Full regions with `startLine`, `endLine`, `startColumn`, `endColumn`.

**Open Source (SCA):** Points to the manifest file (e.g., `package.json`), `startLine` always 1. No `uriBaseId`. No real source location — SCA vulnerabilities are in dependencies, not code.

### Fingerprints

**Code only.** Uses `fingerprints` (not `partialFingerprints`) with keys `"0"` (SHA-256 hash) and `"1"` (dot-separated compound hash). Open Source has no fingerprints.

### Notable Properties

- `rule.properties.exampleCommitFixes` — real commit diffs showing fixes (Code only)
- `rule.properties.cwe` — CWE IDs as array
- `result.properties.priorityScore` — numeric priority (Code only)
- `rule.help.text` — always empty string `""`; all help in `help.markdown`

### Quirks

- `--json` and `--sarif` produce identical output for Snyk Code (the JSON IS SARIF)
- Non-zero exit code on findings — kills CI unless `continue-on-error: true`
- Code filters to security rules only; quality rules excluded from SARIF
- IaC `--sarif` flag unreliable in GitHub Actions; `--sarif-file-output` more reliable

Sources: [Snyk CLI docs](https://docs.snyk.io/snyk-cli/commands/test), [snyk-labs/nodejs-goof](https://github.com/snyk-labs/nodejs-goof), [snyk/code-client-go sarif package](https://pkg.go.dev/github.com/snyk/code-client-go/sarif), [github/codeql-action#2187](https://github.com/github/codeql-action/issues/2187)

---

## SonarCloud / SonarQube

### No Native SARIF Export

Neither SonarCloud nor SonarQube Server produce SARIF natively. SARIF has been a [feature request since 2024](https://community.sonarsource.com/t/export-to-sarif/119918) but SonarSource considers it low priority. All SARIF output comes from the third-party [`sonar-tools`](https://github.com/okorach/sonar-tools) converter, which reads the SonarQube Web API.

SonarQube does *import* SARIF (since Server 9.8, Cloud 2024). This is well-documented but is the opposite direction.

### sonar-tools SARIF Structure

| Field | Value |
|-------|-------|
| Driver name | `"SonarQube"` |
| Rules array | **Not populated** — no rule definitions |
| Results | One per finding from `api/issues/search` |
| ruleId | Full SonarQube key including language prefix |

### ruleId Format

`repository:ruleNumber` — the repository key is language-specific:

| Language | Examples |
|----------|----------|
| Java | `java:S1234` |
| C# | `csharpsquid:S2259` |
| JavaScript | `javascript:S1481` |
| TypeScript | `typescript:S1234` |
| Python | `python:S6540` |

### Severity Mapping

sonar-tools collapses SonarQube's rich severity model to two SARIF levels:

| SonarQube Condition | SARIF `level` |
|---------------------|---------------|
| BUG (any severity) | `error` |
| VULNERABILITY (any severity) | `error` |
| CODE_SMELL + BLOCKER/CRITICAL/MAJOR | `error` |
| CODE_SMELL + MINOR/INFO | `warning` |
| SECURITY_HOTSPOT | `warning` |

`note` is **never used**. The original Sonar severity/type are preserved in the `properties` bag.

### File Paths

`file:///` + relative path (e.g., `file:///src/main/java/Foo.java`). This is technically malformed — `file:///` implies absolute. No `uriBaseId`. `index: 0` hardcoded (references a nonexistent `artifacts` array).

### Known Issues

- **No `rules[]` array** — consumers can't look up rule metadata from the SARIF alone
- **No fingerprints** — no `partialFingerprints` or `fingerprints`
- **No flows** — SonarQube has rich secondary locations and execution flows, but sonar-tools drops them
- **No fixes** — not included
- **Column off-by-one** — SonarQube returns 0-based offsets; sonar-tools maps them as 1-based without adding 1
- **Single run for all projects** — no per-project separation
- **Verbose properties** — all SonarQube metadata dumped into `properties` by default; `--sarifNoCustomProperties` suppresses

### SonarQube Dual Severity Model (context)

SonarQube 10.2+ has two coexisting models:

| Model | Types | Severities |
|-------|-------|------------|
| Standard (legacy) | BUG, VULNERABILITY, CODE_SMELL, SECURITY_HOTSPOT | BLOCKER, CRITICAL, MAJOR, MINOR, INFO |
| MQR (new default) | SECURITY, RELIABILITY, MAINTAINABILITY | BLOCKER, HIGH, MEDIUM, LOW, INFO |

The original Sonar severity is preserved in `properties.severity` regardless of which model is active.

Sources: [sonar-tools source (findings.py)](https://github.com/okorach/sonar-tools/blob/master/sonar/findings.py), [sonar-tools source (findings_export.py)](https://github.com/okorach/sonar-tools/blob/master/cli/findings_export.py), [SonarQube SARIF Import Docs](https://docs.sonarsource.com/sonarqube-server/analyzing-source-code/importing-external-issues/importing-issues-from-sarif-reports), [Sonar Community SARIF export request](https://community.sonarsource.com/t/export-to-sarif/119918)

---

## Qodana

### Native SARIF (richest output)

Qodana produces `qodana.sarif.json` natively — it's one of its primary output formats. The output is the richest of all producers examined.

### Architecture: Rules on Extensions

Unlike every other producer, Qodana puts **all rules on `tool.extensions`**, not `tool.driver.rules`. The driver's `rules` array is always empty. Each extension corresponds to an IntelliJ plugin (e.g., `com.intellij.java`, `org.jetbrains.kotlin`). Consumers that only look at `tool.driver.rules` will find nothing.

The driver does carry a `taxa` array defining the category hierarchy (e.g., `"Java/Probable bugs"`, `"Kotlin/Style issues"`).

### ruleId Format

IntelliJ inspection IDs — PascalCase, not namespaced by language:

```
LongLine, CascadeIf, KotlinUnusedImport, MemberVisibilityCanBePrivate,
IgnoreResultOfCall, JSVoidFunctionReturnValueUsed, UnusedSymbol
```

Language categorization is via taxa relationships, not the ruleId itself.

### Linter Codes (driver name)

| Code | Product |
|------|---------|
| `QDJVM` | Java/Kotlin/Groovy |
| `QDJS` | JavaScript/TypeScript |
| `QDNET` | C#/F#/VB.NET |
| `QDPY` | Python |
| `QDGO` | Go |
| `QDPHP` | PHP |

### Severity: Three Systems

Qodana carries three severity representations per finding:

| System | Field | Values |
|--------|-------|--------|
| SARIF | `result.level` | `error`, `warning`, `note` |
| IntelliJ | `properties.ideaSeverity` | `ERROR`, `WARNING`, `WEAK WARNING`, `INFORMATION`, `TYPO` |
| Qodana | `properties.qodanaSeverity` | `Critical`, `High`, `Moderate`, `Low`, `Info` |

Priority for resolution: `qodanaSeverity` > `ideaSeverity` > SARIF `level` > default `Moderate`.

### File Paths

Relative paths with `uriBaseId: "SRCROOT"` (no `originalUriBaseIds` defined to resolve it — SRCROOT is an implicit convention).

### Regions

Rich but incomplete: `startLine`, `startColumn`, `charOffset`, `charLength`, `snippet.text`, `sourceLanguage` — but **no `endLine` or `endColumn`**. End position must be computed from `charLength`. `contextRegion` provides surrounding lines.

### Fingerprints

`partialFingerprints` with key `"equalIndicator/v1"` (SHA-256, 64 hex chars). Present on 100% of results. Used for baseline comparison. Does not use `fingerprints` (the non-partial variant).

### Notable Properties

- `baselineState` — `"new"`, `"unchanged"`, `"updated"`, `"absent"` (only when run with `--baseline`)
- `rule.defaultConfiguration.parameters.cweIds` — CWE identifiers as floats (`[252.0, 563.0]`)
- `rule.defaultConfiguration.parameters.suppressToolId` — IntelliJ suppress annotation ID
- `run.properties.qodana.sanity.results` — sanity check results (same schema)
- `run.properties.qodana.promo.results` — promotional results from higher-tier features
- `logicalLocations` — module-level location (`kind: "module"`)
- `versionControlProvenance` — repo URI, revision ID, branch, author info

### Quirks

- Rules on extensions, not driver (breaks naive consumers)
- No `originalUriBaseIds` despite using `uriBaseId`
- `cweIds` serialized as floats
- `sourceLanguage` inconsistent casing (`"kotlin"` vs `"JAVA"` vs `"ECMAScript 6"`)
- `kind` always `"fail"` (never uses `pass`, `open`, `informational`, etc.)
- No `fixes`, `relatedLocations`, or `codeFlows`
- Single location per result (multi-file issues not linked)

Sources: [Qodana SARIF output docs](https://www.jetbrains.com/help/qodana/qodana-sarif-output.html), [JetBrains/qodana-sarif](https://github.com/JetBrains/qodana-sarif), [JetBrains/projector-server qodana.sarif.json](https://github.com/JetBrains/projector-server/blob/master/qodana.sarif.json), [JetBrains/kotlin-web-site qodana.sarif.json](https://github.com/JetBrains/kotlin-web-site/blob/master/qodana.sarif.json)

---

## Other Producers

### CodeQL (GitHub)

The reference implementation — GitHub staff were heavily involved in the SARIF spec. Richest spec compliance.

- **ruleId:** `language/rule-name` (e.g., `cpp/unsafe-format-string`, `js/xss`)
- **Fingerprints:** `partialFingerprints` with `primaryLocationLineHash` (rolling polynomial hash of first 100 non-whitespace chars at primary location) and `primaryLocationStartColumnFingerprint`. GitHub uses only the line hash for deduplication.
- **codeFlows:** Populated for `@kind path-problem` queries. Up to 10,000 threadFlow locations per result.
- **relatedLocations:** Populated when messages have placeholders.
- **suppressions:** `IN_SOURCE` when suppressed.
- **File paths:** Relative with `uriBaseId`.
- **One run per language** analyzed.

Sources: [CodeQL CLI SARIF output - GitHub Docs](https://docs.github.com/en/code-security/codeql-cli/using-the-advanced-functionality-of-the-codeql-cli/sarif-output), [codeql-action/src/fingerprints.ts](https://github.com/github/codeql-action/blob/main/src/fingerprints.ts)

### Semgrep

- **ruleId:** Dot-separated hierarchical path (e.g., `python.lang.security.dangerous-system-call.dangerous-system-call`)
- **Fingerprints:** `fingerprints` (not `partialFingerprints`) with `matchBasedId/v1` (since v0.120.0)
- **`security-severity` as text string** — emits `"Medium"` instead of numeric `"5.5"`. GitHub rejects this.
- No `fixes` in SARIF despite having `fix:` in rule YAML.

Sources: [Semgrep SARIF fields docs](https://semgrep.dev/docs/semgrep-appsec-platform/json-and-sarif), [semgrep#5729](https://github.com/semgrep/semgrep/issues/5729), [semgrep#10834](https://github.com/semgrep/semgrep/issues/10834)

### Roslyn Analyzers

- **ruleId:** Standard diagnostic IDs (`CS0168`, `CA1822`, `IDE0001`, `SA1000`)
- **File paths:** Absolute `file://` URIs (e.g., `file:///C:/source/repos/Class1.cs`)
- **Fingerprints:** None.
- **Suppressions:** Rich — three types (`PragmaDirective`, `SuppressMessageAttribute`, `DiagnosticSuppressor`) with justification text.
- **columnKind:** `utf16CodeUnits` (explicitly set).
- **Spec violations:** Inconsistent `suppressions` presence across results, empty `tool.driver.language` string.

Sources: [Roslyn Error Log Format](https://github.com/dotnet/roslyn/blob/main/docs/compilers/Error%20Log%20Format.md), [dotnet/roslyn#62894](https://github.com/dotnet/roslyn/issues/62894), [dotnet/roslyn#68745](https://github.com/dotnet/roslyn/issues/68745)

### Trivy

- **ruleId:** CVE identifiers for vulnerabilities (`CVE-2022-28346`), `AVD-*` for misconfigurations
- **File paths:** Relative with `uriBaseId: "ROOTPATH"`. URL-encoded. Multiple known path bugs (prepended `library/`, truncated parentheses, invalid `git::` scheme, empty URIs).
- **Fingerprints:** None. [Open feature request](https://github.com/aquasecurity/trivy/discussions/4432).
- **security-severity:** Numeric CVSS scores. Correct format.
- **Columns:** Hardcoded to 1.

Sources: [Trivy SARIF docs](https://trivy.dev/docs/latest/configuration/reporting/), [trivy#2960](https://github.com/aquasecurity/trivy/issues/2960), [trivy#5003](https://github.com/aquasecurity/trivy/issues/5003)

---

## Cross-Cutting Findings

### What Is Universally Present

Every producer examined populates these fields (note: `ruleId`, `level`, `locations`, and `fingerprints` are all optional per the SARIF spec — universal presence here reflects the 7 producers examined, not a spec guarantee):

- `version` — always `"2.1.0"`
- `runs[].tool.driver.name`
- `result.ruleId`
- `result.message.text`
- `result.level` (on result or via `defaultConfiguration.level` on rule)
- `locations[].physicalLocation.artifactLocation.uri`
- `locations[].physicalLocation.region.startLine`

### What Is Rarely Used

| Feature | Only used by |
|---------|-------------|
| `codeFlows` | CodeQL (path-problem queries), Snyk Code |
| `suppressions` | Roslyn, Snyk Code (conditional) |
| `relatedLocations` | CodeQL |
| `baselineState` | Qodana (with `--baseline`) |
| `fixes` | **Nobody** — no producer examined populates this |
| `graphs` / `graphTraversals` | Nobody |
| `taxonomies` | Nobody (Qodana uses taxa on driver but not the formal mechanism) |

### The Fingerprint Landscape

| Tool | Field | Key | Algorithm |
|------|-------|-----|-----------|
| CodeQL | `partialFingerprints` | `primaryLocationLineHash` | Rolling polynomial hash of 100 non-whitespace chars |
| Qodana | `partialFingerprints` | `equalIndicator/v1` | SHA-256 |
| Snyk Code | `fingerprints` | `"0"`, `"1"` | SHA-256, compound hash |
| Semgrep | `fingerprints` | `matchBasedId/v1` | Undocumented |
| Snyk OSS | None | — | — |
| ESLint | None | — | — |
| Roslyn | None | — | — |
| Trivy | None | — | — |

Two different SARIF fields (`fingerprints` vs `partialFingerprints`), five different key names, at least three different algorithms. Half the producers generate no fingerprints at all. GitHub's upload action auto-calculates `primaryLocationLineHash` when missing, but the REST API does not.

### The Path Problem

| Tool | Style | Scheme | uriBaseId |
|------|-------|--------|-----------|
| CodeQL | Relative | None | Yes |
| Snyk Code | Relative | None | `%SRCROOT%` |
| Snyk OSS | Relative (manifest only) | None | None |
| Qodana | Relative | None | `SRCROOT` |
| Semgrep | Relative | None | Yes |
| Trivy | Relative | None | `ROOTPATH` |
| sonar-tools | Relative with `file:///` prefix (malformed) | `file` | None |
| Roslyn | Absolute | `file` | None |
| ESLint | Absolute | (varies) | None |

Three `uriBaseId` conventions (`%SRCROOT%`, `SRCROOT`, `ROOTPATH`), two absolute path approaches, one malformed hybrid. Most don't define `originalUriBaseIds` to resolve their base IDs.

### The Severity Problem

| Tool | Uses `error` | Uses `warning` | Uses `note` | Uses `none` |
|------|-------------|----------------|-------------|-------------|
| Snyk | High, Critical | Medium | Low | No |
| sonar-tools | BUG/VULN + Major+ | Minor/Info smells | **Never** | No |
| Qodana | ERROR | WARNING | WEAK WARNING, INFO, TYPO | No |
| CodeQL | Per query severity | Per query severity | Per query severity | No |
| Semgrep | Per rule severity | Per rule severity | Per rule severity | No |
| ESLint | error (2) | warning (1) | **Never** | No |
| Roslyn | error | warning | note (info diags) | No |
| Trivy | CRITICAL, HIGH | MEDIUM | LOW, UNKNOWN | Other |

Several tools (sonar-tools, ESLint) never emit `note`. Tools that carry richer internal severity models (Snyk, Qodana, SonarQube) collapse them into three SARIF levels and preserve the original in properties.

### Where Rules Live

| Tool | `tool.driver.rules[]` | `tool.extensions[].rules[]` |
|------|----------------------|----------------------------|
| Most tools | Yes | No |
| Qodana | **Empty** | **Yes** (all rules here) |
| sonar-tools | **Empty** | No |

Qodana is the only producer that puts rules exclusively on extensions. sonar-tools doesn't emit rules at all. A robust consumer must check both locations.

---

## GitHub Upload Requirements (de facto standard)

GitHub is the largest SARIF consumer. Their requirements function as a compatibility baseline.

**Required:** `version`, `runs[]`, `tool.driver.name`, `result.ruleId`, `result.message.text`, at least one location with `artifactLocation.uri` and `region.startLine`.

**Strongly recommended:** `partialFingerprints.primaryLocationLineHash`, `defaultConfiguration.level`, `properties.security-severity` (numeric 0.1–10.0), `help.markdown`.

**Size limits:** 10 MB compressed, 25,000 results per run (top 5,000 displayed), 1,000 locations per result, 20 runs per file.

Source: [SARIF support for code scanning - GitHub Docs](https://docs.github.com/en/code-security/code-scanning/integrating-with-code-scanning/sarif-support-for-code-scanning)

---

## Microsoft SARIF SDK

The [Microsoft SARIF SDK](https://github.com/microsoft/sarif-sdk) (NuGet: `Microsoft.CodeAnalysis.Sarif`, MIT license) provides strongly-typed C# classes for the full SARIF object model, plus validation, version conversion (v1 → v2.1), and a CLI tool (`Sarif.Multitool`) for merge, rewrite, and normalization. The `--normalize-for-ghas` flag on `rewrite` handles GitHub-specific compatibility. The SDK normalizes SARIF structure but does not normalize semantic content across tools (e.g., won't convert text severity to numeric).

Source: [microsoft/sarif-sdk](https://github.com/microsoft/sarif-sdk)

---

## Gaps in This Research

- **Container scan SARIF** (Snyk Container, Trivy container mode) not deeply examined — samples harder to find.
- **IaC scan SARIF** (Snyk IaC, Trivy IaC) only partially examined.
- **Checkov, Bandit, SpotBugs** and other producers not examined.
- **SARIF v2.2 draft** exists but is not yet an OASIS standard; not examined.
- **sonar-tools accuracy** — it's a third-party converter, not SonarSource's. Quality depends on one maintainer.
- **Fingerprint stability** across tool versions not verified — do the same findings keep the same fingerprints when the tool updates?

---

*Seven tools, one spec, seven interpretations. The universal subset is small: ruleId, message, level, startLine, file path. Everything else is "it depends."*
