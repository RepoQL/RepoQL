---
description: URI pattern matching with glob_files() and matches_glob() - wildcards, compound patterns, exclusions, and fragment matching
tags: ["GlobFiles", "MatchesGlob", "PathWildcards", "CompoundPatterns", "ExclusionPatterns", "FragmentPatterns", "SymbolWildcards"]
audience: ["LLMs", "Humans"]
categories: ["Reference[100%]", "Query-Functions[95%]"]
---

# URI Globbing Patterns

Match files and symbols using familiar glob syntax extended for RepoQL URIs.

## Quick Reference

```sql
-- Files by pattern
SELECT * FROM glob_files('src/**/*.cs');

-- Predicate in WHERE
SELECT * FROM node WHERE matches_glob(uri, 'src/**;!tests/**');

-- Symbol patterns
SELECT * FROM glob_files('src/Auth.cs#symbol=AuthService.*');
```

---

## Capsule: GlobFiles

**Invariant**
`glob_files(pattern)` returns URIs matching the pattern as a table.

**Example**
```sql
SELECT uri FROM glob_files('src/**/*.cs');                    -- All C# in src
SELECT uri FROM glob_files('src/**;lib/**');                  -- src OR lib
SELECT uri FROM glob_files('src/**;!src/tests/**');           -- Exclude tests
SELECT uri FROM glob_files('src/Foo.cs#symbol=MyClass.*');    -- Direct members
```
//BOUNDARY: Without `#fragment`, returns documents only; with fragment, returns matching nodes.

**Depth**
- Returns table with single `uri` column
- Parameters: `pattern_spec`, `ignore_case := TRUE`, `default_scheme := 'file:///'`
- Shorthand `src/**` expands to `file:///src/**`
- SeeAlso: `MatchesGlob`, `FragmentPatterns`

---

## Capsule: MatchesGlob

**Invariant**
`matches_glob(uri, pattern)` tests if a URI matches, returning true/false/null.

**Example**
```sql
-- Filter nodes by pattern
SELECT uri FROM node
WHERE matches_glob(uri, 'file:///src/**/*.cs;!file:///src/tests/**');

-- Pattern in CASE
SELECT uri,
       CASE WHEN matches_glob(uri, '**/test*') THEN 'test' ELSE 'src' END AS category
FROM node WHERE kind = 'document';
```
//BOUNDARY: Returns NULL for null/blank URI (SQL three-valued logic).

**Depth**
- Use in WHERE, CASE, JOIN conditions
- Parameters: `uri`, `pattern`, `ignore_case := TRUE`, `default_scheme := 'file:///'`
- Distinction: `glob_files` returns rows; `matches_glob` is a predicate
- SeeAlso: `GlobFiles`, `CompoundPatterns`

---

## Capsule: PathWildcards

**Invariant**
`*` matches within one segment, `**` matches across segments, `?` matches one character.

**Example**
```sql
SELECT * FROM glob_files('src/*.cs');        -- src/File.cs (not src/sub/File.cs)
SELECT * FROM glob_files('src/**/*.cs');     -- src/File.cs AND src/sub/File.cs
SELECT * FROM glob_files('src/???.cs');      -- src/Foo.cs (exactly 3 chars)
SELECT * FROM glob_files('src/**/test*');    -- Any file starting with "test"
```
//BOUNDARY: `**` must be a complete segment; `src/**test` is invalid.

**Depth**
- `*` = any chars except `/`
- `**` = any path depth (zero or more segments)
- `?` = exactly one char except `/`
- Case insensitive by default (`ignore_case := TRUE`)
- NotThis: regex syntax (`.*` means something different)

---

## Capsule: CompoundPatterns

**Invariant**
Semicolon joins multiple patterns; URI matches if ANY positive pattern matches.

**Example**
```sql
-- Multiple includes (OR)
SELECT * FROM glob_files('src/**;lib/**;tools/**');

-- Mixed schemes
SELECT * FROM glob_files('file:///src/**;docs:///**');
```

**Depth**
- Patterns separated by `;`
- Order doesn't matter for positive patterns
- Whitespace around `;` is trimmed
- Empty pattern (blank string) matches everything
- SeeAlso: `ExclusionPatterns`

---

## Capsule: ExclusionPatterns

**Invariant**
`!pattern` excludes matches globally across all includes.

**Example**
```sql
-- Exclude tests from all source
SELECT * FROM glob_files('src/**;lib/**;!**/test*;!**/Mock*');

-- Only negative patterns = exclude from everything
SELECT * FROM glob_files('!**/*.generated.cs');
```
//BOUNDARY: Exclusions are global; `a;!b;c` excludes `b` from both `a` and `c`.

**Depth**
- `!` prefix marks exclusion
- Applied AFTER positive pattern matching
- Multiple exclusions: all are applied (AND logic)
- Only negatives = match everything except those patterns
- NotThis: `!` anywhere except pattern start

---

## Capsule: FragmentPatterns

**Invariant**
`#symbol=` and `#line=` match sub-document entities, not just files.

**Example**
```sql
-- All methods in a class
SELECT * FROM glob_files('src/Auth.cs#symbol=AuthService.*');

-- Specific line range
SELECT * FROM glob_files('src/**/*.cs#line=1,50');

-- Handlers across codebase
SELECT * FROM glob_files('src/**/*.cs#symbol=*Handler');
```
//BOUNDARY: Fragment patterns return nodes (symbols, ranges), not documents.

**Depth**
- `#symbol=Name` matches qualified symbol names
- `#line=N` or `#line=N,M` matches line ranges
- Fragment wildcards: `#symbol=*`, `#line=*`
- Combine with path: `src/**/*.cs#symbol=*Service`
- SeeAlso: `SymbolWildcards`

---

## Capsule: SymbolWildcards

**Invariant**
`.*` matches direct children; `.**` matches all descendants in symbol hierarchy.

**Example**
```sql
-- Direct members only (methods, fields, nested types)
SELECT * FROM glob_files('src/Foo.cs#symbol=MyClass.*');
-- Matches: MyClass.Method, MyClass.Field
-- Not: MyClass.Inner.Method

-- All descendants at any depth
SELECT * FROM glob_files('src/Foo.cs#symbol=MyClass.**');
-- Matches: MyClass.Method, MyClass.Inner.Method, MyClass.Inner.Deep.Value
```
//BOUNDARY: The class itself (`MyClass`) is not matched by either wildcard.

**Depth**
- `.*` = one level deep (no dots in suffix)
- `.**` = any depth (one or more dots allowed)
- Mirrors path wildcards: `*` vs `**`
- Case insensitive
- NotThis: `*` alone in symbol (use `.*` for children)

---

## Common Patterns

| Goal | Pattern |
|------|---------|
| All C# files | `**/*.cs` |
| Source excluding tests | `src/**;!**/test*;!**/Test*` |
| All handlers | `**/*.cs#symbol=*Handler` |
| Class members | `File.cs#symbol=ClassName.*` |
| All descendants | `File.cs#symbol=Namespace.**` |
| Multiple directories | `src/**;lib/**;tools/**` |
| Specific extensions | `**/*.{cs,ts,js}` |
| Line ranges | `File.cs#line=10,50` |

---

## Integration with Tools

### read tool
```
read("src/**/*.cs;!**/tests/**", 5000)
read("src/Auth.cs#symbol=AuthService.*", 2000)
```

### xray tool
```
xray(intent="Find", scope="src/**/*.cs;!tests/**", keywords="authentication")
```

### SQL queries
```sql
-- Combine with search
SELECT g.uri, s.score
FROM glob_files('src/**/*.cs') g
JOIN search('error handling', k := 100) s ON g.uri = s.uri;

-- With snippet
SELECT g.uri, sn.text
FROM glob_files('src/**/*.cs#symbol=*Exception') g,
     LATERAL snippet(g.uri, 2) sn;
```

---

## Implementation Notes

- Patterns compiled to regex and cached for performance
- Three-valued logic: null URI → null result
- Default scheme `file:///` applied to bare paths
- Fragment matching uses `SymbolPatternMatcher` for symbol wildcards
- Compound patterns parsed by `UriPatternMatcher`

---

## Quick Checklist

- [ ] Use `**` for recursive, `*` for single-level
- [ ] Prefix exclusions with `!`
- [ ] Join patterns with `;`
- [ ] Add `#symbol=` for symbol matching
- [ ] Use `.*` for direct children, `.**` for all descendants
- [ ] Fragment patterns return nodes, not documents
