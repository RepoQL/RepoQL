---
description: JSON format support — four incremental plans from core parser through secret detection
tags: [format, json, plan, overview]
audience: { human: 70, agent: 30 }
purpose: { plan: 60, gestalt: 40 }
---

# JSON Format Plans

Implements: [JSON Format Design](../../designs/json-format.md)

Four increments, each independently buildable, testable, and valuable.

| Plan | What | Enables |
|------|------|---------|
| [01 — Structure Parser](01-structure-parser.md) | `JsonStructureParser`, key tree, shape detection, line tracking | Foundation for all JSON indexing |
| [02 — Generic JSON Tier](02-generic-json-tier.md) | Classifier, parser, loader, templates, SQL macros, registration | `SELECT * FROM json_files()` — no JSON file is plain text |
| [03 — JSONC Support](03-jsonc-support.md) | `JsonNormalizer`, comment stripping, source map | `tsconfig.json` and friends get real structure |
| [04 — Secret Detection](04-secret-detection.md) | `JsonSecretDetector`, pattern matching, annotations | `SELECT * FROM annotation WHERE rule_id = 'json.potential-secret'` |

## Dependency Chain

```
01 ──→ 02 ──→ 03
         ↘
          04
```

Plans 03 and 04 are independent of each other — both depend on 02 and can be built in parallel.

## What Exists Today

`AppSettingsLoader` handles `appsettings*.json` with config-specific structure. All other `.json` files fall through to `PlainTextParser`. After all four plans, every JSON file gets a key tree, pointer addresses, meaningful headlines, queryable structure, and secret annotations.
