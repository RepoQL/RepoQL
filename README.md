# RepoQL

Queryable code intelligence for AI coding agents.

[Official website](https://repoql.com) · [Codex plugin](plugins/repoql-codex/README.md) · [Cursor plugin](plugins/repoql-cursor/README.md)

## Install

By downloading and using RepoQL, you agree to the [Customer Terms and Software Licence](https://repoql.com/terms/2026-09-11/).

**Codex** — [install the RepoQL Codex plugin](plugins/repoql-codex/README.md).

**Cursor** — [install the RepoQL Cursor plugin](plugins/repoql-cursor/README.md).

**Claude Code** — one-step plugin install (downloads the `rql` binary automatically on first session):

```
/plugin marketplace add RepoQL/RepoQL
/plugin install repoql@repoql-plugins
```

**Any other agent, or standalone:**

**macOS / Linux**

By downloading and using RepoQL, you agree to the [Customer Terms and Software Licence](https://repoql.com/terms/2026-09-11/).

```sh
curl -fsSL https://downloads.repoql.ai/latest/install-rql.sh | bash
```

**Windows (PowerShell)**

By downloading and using RepoQL, you agree to the [Customer Terms and Software Licence](https://repoql.com/terms/2026-09-11/).

```powershell
irm https://downloads.repoql.ai/latest/install-rql.ps1 | iex
```

Then `rql --version` to verify, and `rql install` to wire it into your AI agent.
