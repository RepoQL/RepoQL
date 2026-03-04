---
description: "List all registered commands with descriptions."
tags: ["command", "help", "list", "discovery"]
audience: ["LLMs"]
categories: ["Reference[100%]", "Commands[100%]"]
---

# ::?

List all registered commands. Quick way to discover what's available.

---

## Capsule: BasicUsage

**Invariant**
`::?` lists every registered command with its description.

**Example**
```
::?
→ Available commands:
    ::?                List all commands
    ::diagnostics      Run full system health diagnostics
    ::diagnostics.fast Run quick health checks
    ::diagnostics.memory Show host memory breakdown
    ::host.restart     Restart the repository host
    ::reindex          Reindex files, optionally scoped to a URI pattern
    ::repo             Switch to a different repository

  Use ::command --help for usage details.
```

**Depth**
- Commands are auto-discovered from `[CommandClass]` / `[Command]` attributes
- Use `::command --help` for detailed usage of any specific command
- Prefix matching also works: `::host --help` lists all `host.*` subcommands

---

## Help

```
::? --help
→ ::? — List all commands
  Usage: ::?
```
