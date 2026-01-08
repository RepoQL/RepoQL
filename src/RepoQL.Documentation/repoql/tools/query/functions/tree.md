---
description: "read('<glob> => tree', budget) → directory tree of matching files. Progressive: full tree → folders-only → budget info."
tags: ["tree", "directory", "structure", "files", "folders", "read"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Tools[100%]"]
---

# Tree Format

Display matching files as a directory tree via the read tool.

---

## Capsule: TreeSyntax

**Invariant**
`read("<glob> => tree", budget)` renders matched files as a tree structure.

**Example**
```
read("file:///src/** => tree", 2000)
read("file:///src/**/*.cs => tree", 1500)
read("file:///** => tree", 5000)
```
//BOUNDARY: Use `=> tree` suffix after glob pattern. Budget controls tree detail level.

**Depth**
- Syntax: `<glob_pattern> => tree`
- Space around `=>` optional
- All glob features work: `**`, `*`, `;`, `!`

---

## Capsule: TreeOutput

**Invariant**
Output is ASCII tree with directories and files. Folders show `/` suffix.

**Example**
```
src/
├── Services/
│   ├── AuthService.cs
│   ├── UserService.cs
│   └── OrderService.cs
├── Models/
│   ├── User.cs
│   └── Order.cs
└── Program.cs
```
//BOUNDARY: Tree sorted alphabetically. Directories before files at each level.

---

## Capsule: ProgressiveDisclosure

**Invariant**
Budget controls detail: full tree → folders-only → budget exceeded message.

**Example**
```
-- Budget fits full tree
read("file:///src/** => tree", 5000)
→ Full tree with all files

-- Budget fits folders only
read("file:///src/** => tree", 500)
→ Folders tree + "[Showing folders only (487 tokens). Full tree with files: 2341 tokens]"

-- Budget too small
read("file:///src/** => tree", 100)
→ "Tree output exceeds budget (487 tokens needed, 100 budget)..."
```
//BOUNDARY: Folders-only collapses files into counts per directory.

**Depth**
- Full tree: Every file listed
- Folders-only: Directories with file counts by type
- Exceeded: Info message with required budget amounts

---

## Capsule: FoldersOnlyFormat

**Invariant**
Folders-only shows directory structure with aggregated file counts.

**Example**
```
src/
├── Services/           [3 .cs]
├── Models/             [2 .cs]
├── Controllers/        [5 .cs, 1 .json]
└── Tests/              [8 .cs]
```
//BOUNDARY: Extension counts help understand content without listing every file.

---

## Capsule: WithGlobPatterns

**Invariant**
All glob features work with tree: wildcards, compounds, exclusions.

**Example**
```
-- All C# files
read("file:///src/**/*.cs => tree", 2000)

-- Multiple directories
read("file:///src/**;file:///lib/** => tree", 3000)

-- Exclude tests
read("file:///src/**;!**/test*;!**/Test* => tree", 2000)

-- Specific depth
read("file:///src/*/*.cs => tree", 1000)  -- one level deep only
```
//BOUNDARY: Exclusions reduce tree size, helping fit in budget.

---

## Capsule: WhenToUse

**Invariant**
Use tree to understand codebase structure before diving into specific files.

**Example**
```
-- Explore unfamiliar repo
read("file:///** => tree", 3000)

-- Understand module structure
read("file:///src/Services/** => tree", 1500)

-- Then read specific files
read("file:///src/Services/AuthService.cs", 4000)
```
//BOUNDARY: Tree first → identify targets → read specific files.

**Depth**
- Codebase orientation
- Finding file locations
- Understanding project layout
- Cheaper than xray Explore for pure structure

---

## Common Patterns

| Goal | Command |
|------|---------|
| Full repo structure | `read("file:///** => tree", 5000)` |
| Source files only | `read("file:///src/** => tree", 2000)` |
| Specific extension | `read("file:///**/*.md => tree", 1000)` |
| Exclude tests | `read("file:///src/**;!**/test** => tree", 2000)` |
| Multiple roots | `read("file:///src/**;file:///lib/** => tree", 3000)` |
