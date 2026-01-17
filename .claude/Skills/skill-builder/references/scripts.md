# Scripts in Skills

Scripts are executables that extend a skill's capabilities. Only their output consumes tokens—the script itself is free.

## When to Use Scripts

| Instead of... | Use a script when... |
|---------------|---------------------|
| Documenting data that changes | Source of truth is queryable |
| Describing complex steps | Automation reduces error |
| Trusting compliance | Verification can be automated |
| Static examples | Generated output is more useful |

## How Scripts Serve Each Zone

**Knowledge**: Fetch current truth, avoid rot
```
scripts/list-api-endpoints.sh
→ Current endpoints from OpenAPI spec
```

**Process**: Execute steps, verify outcomes
```
scripts/migrate.sh
scripts/rollback.sh
→ Automated execution with clear success/failure
```

**Constraint**: Verify compliance
```
scripts/scan-for-secrets.sh
→ Violations found, or "No violations"
```

**Wisdom**: Generally not applicable—wisdom is about thinking, not executing.

## Best Practices

### Clear Interface

Document what the script expects and produces:

```bash
#!/bin/bash
# Usage: list-endpoints.sh [service-name]
# Output: JSON array of endpoint definitions
# Requires: jq, curl, API_BASE_URL env var
```

### Predictable Output

Claude reads the output. Make it parseable:

```bash
# Good: structured, clear
echo "Status: SUCCESS"
echo "Migrations applied: 3"
echo "Duration: 2.4s"

# Bad: noisy, ambiguous
echo "Done!"
```

### Explicit Failure

Exit codes and error messages should be clear:

```bash
if [ $? -ne 0 ]; then
    echo "Status: FAILED"
    echo "Error: Migration 003 failed - foreign key constraint"
    exit 1
fi
```

### Idempotent Where Possible

Scripts that can be safely re-run are more useful:

```bash
# Idempotent: checks before acting
if migration_applied "003"; then
    echo "Migration 003 already applied, skipping"
else
    apply_migration "003"
fi
```

### Minimal Dependencies

Scripts that require complex setup are fragile:

- Prefer standard tools (bash, curl, jq)
- Document required dependencies
- Fail fast with clear message if dependencies missing

### Platform Awareness

Scripts run on different platforms. Consider:

| Platform | Shell | Path separator | Line endings | Common tools |
|----------|-------|----------------|--------------|--------------|
| macOS | bash/zsh | `/` | LF | BSD variants |
| Linux | bash | `/` | LF | GNU variants |
| Windows | PowerShell/cmd | `\` | CRLF | Different ecosystem |
| WSL | bash | `/` | LF | GNU variants |

**If writing for a specific platform:**
```bash
#!/bin/bash
# Platform: macOS/Linux only
# Note: Uses GNU tools; may need brew install on macOS
```

**If writing for multiple platforms:**
- Consider Python or Node for cross-platform consistency
- Or provide platform-specific variants:
  ```
  scripts/
  ├── deploy.sh      # macOS/Linux
  └── deploy.ps1     # Windows
  ```

**If you don't know the target platform:**
Ask the user. Don't assume. A script that works perfectly on Linux may fail silently or destructively on Windows.

```
Before writing this script, I need to know:
- What platform(s) will this run on? (macOS, Linux, Windows, WSL)
- Are there any tool constraints? (must use bash, PowerShell preferred, etc.)
```

### No Side Effects Without Consent

Scripts that modify state should be explicit:

```bash
# Dangerous: modifies without warning
drop_all_tables

# Better: requires confirmation or dry-run default
echo "This will drop all tables. Pass --confirm to proceed."
echo "Dry run: would drop tables: users, orders, payments"
```

## Script Location

```
skill-name/
├── SKILL.md
├── reference.md
└── scripts/
    ├── fetch-endpoints.sh
    ├── validate-config.sh
    └── run-migration.sh
```

## Anti-Patterns

**Scripts as documentation**
```bash
# Bad: just echoes what could be in markdown
echo "To deploy, run: kubectl apply -f deploy.yaml"
```

**Unparseable output**
```bash
# Bad: Claude can't reliably extract information
echo "Did some stuff... maybe worked? Check the logs idk"
```

**Silent failure**
```bash
# Bad: fails without indication
curl $URL || true  # Swallows errors
```

**Environment assumptions**
```bash
# Bad: assumes specific setup
cd ~/projects/myapp  # Breaks for everyone else
```

## The Token Economy

Scripts are powerful because:
- Script content: 0 tokens (not sent to model)
- Script output: tokens consumed

This means a 500-line script that outputs 10 lines costs only 10 lines of context. Use this to your advantage—complex logic in the script, clean output for Claude.

## Final Thought

A script should do one thing and report clearly whether it worked.
