---
name: using-uplinks
description: "Use or set up RepoQL uplinks: shared, remotely hosted indexes that agents and CI can query without local clones. Use when the user mentions an uplink, a team or remote RepoQL index, searching repositories available on another host, or deploying RepoQL on AWS, GCP, or Kubernetes. Also use when session context lists an accessible uplink relevant to repositories missing locally. Covers discovery, explicit remote routing, source scope, access, storage, snapshot preparation, and deployment guidance. Ordinary work confined to the local repository does not need this skill."
---

# Use a shared RepoQL index

An uplink holds a shared, indexed set of repositories and makes it available through RepoQL's cloud front door. Agents can explore unfamiliar services, read symbols, grep text, inspect relationships, and synthesize answers across that host's imports without cloning the estate into each agent workspace. CI can consult the same context during a review. Organization access controls determine who can reach it.

The operations are familiar; the host and source scope are the choices that matter. One request targets one uplink or the local host. It does not automatically combine all accessible hosts.

## Find the host, then its sources

Use the session's **Accessible uplinks** context, or run `rql uplinks` to discover names available to the signed-in account. A request to use an existing index does not require creating an instance or issuing a deployment credential. If the list is empty, check the account and access before assuming the team has no uplink.

Pass the discovered name on **every** remote read-only call: `uplink="team-index"` for MCP, or `--uplink team-index` for CLI. Omit it for local work. Names in these examples are placeholders; use the discovered value.

Discover the repositories on that host:

```text
query(sql="SELECT source_uri, file_count, embed_pct FROM Filesystems ORDER BY source_uri", uplink="team-index")
```

If ownership is unknown, first explore `github://**` on the selected uplink to locate candidate imports, then narrow to the repository URIs returned by the inventory. For a known repository, scope directly to its URI. Use `keywords` for unfamiliar vocabulary, `explore` for the landscape, and `read` for the relevant slices. Keep the uplink argument on follow-ups:

```text
explore(uriGlob="github://acme/platform/**", keywords="authentication", question="Where are access tokens checked?", uplink="team-index")
read(uriGlob="github://acme/platform/** => tree: headlines", uplink="team-index")
```

Equivalent CLI calls:

```sh
rql uplinks
rql query "SELECT source_uri, file_count, embed_pct FROM Filesystems ORDER BY source_uri" --uplink team-index
rql explore "authentication" --uri-glob "github://acme/platform/**" --question "Where are access tokens checked?" --uplink team-index
rql read "github://acme/platform/** => tree: headlines" --uplink team-index
```

`file:///` on an uplink means that host's workspace. It does not refer to the agent's local checkout. A missing uplink argument can produce a plausible answer from the wrong host, so record which host and repositories supplied the evidence.

Readiness and freshness are separate. The response footer reports indexing coverage; `Filesystems.embed_pct` covers structure embeddings, not all content embeddings. Managed Git imports refresh incrementally and indexing is eventually consistent. Local edits or a just-pushed commit may not be represented yet. Check the relevant source and revision before making claims about a specific change.

## Authority follows the operation

The optional uplink route exposes read-only tools. It does not provide a remote shell, redirect local writes, or authorize an import. Use the admin door and the appropriate account authority when the requested work includes importing or managing the estate. Read the current embedded guidance before choosing an administrative command.

Use the member/admin door URLs returned by RepoQL or shown in the portal. Never construct one from an organization display name. Existing sign-in can be reused; only ask for authentication when the operation actually requires it. CI door-token guidance is in `help:///hosting/uplink.md` and `help:///commands/auth.md`.

## Deploy from the shipped guidance

Before preparing infrastructure, read the embedded setup page and the relevant platform recipe. They describe the installed version and contain the deployable assets:

| Need | Read |
|---|---|
| Identity, team access, imports, CPU/memory, verification | `help:///hosting/uplink.md` |
| AWS ECS Fargate and Terraform | `help:///hosting/uplink-aws.md` |
| Google Cloud COS VM and Terraform | `help:///hosting/uplink-gcp.md` |
| Kubernetes snapshot init container | `help:///hosting/uplink-kubernetes.md` |
| Offline snapshot, existing index reuse, restore | `help:///hosting/snapshots.md` |
| Actual scripts and Terraform | `help:///hosting/uplink/**` |

For example, `read(uriGlob="help:///hosting/uplink-aws.md")` retrieves the AWS recipe. Materialize referenced assets with their directory layout intact; Terraform can load sibling startup scripts. If an older binary lacks a page or `snapshot --help`, establish a compatible version before using that recipe. Pin the same tested image for preparation and serving.

The deployment choices with the largest consequences are:

- **Keep the working index fast.** Active clones, the database, and trigram files belong on local or suitable block storage. EFS can hold a compressed archive at `/backup` while `/workspace` stays local. This avoids network filesystem metadata latency on each scan.
- **Prepare, then serve.** Snapshot restores into an empty workspace, refreshes managed imports and seeds, completes indexing, writes an archive, and exits. With an existing workspace, omit restore and reuse its index. It runs a finite indexing host without dashboard, MCP bridge, or health listener. Preparation failure must prevent serving. Follow the platform recipe: Kubernetes supports the separate init-container pattern; the AWS example runs the long preparation in the main container after a short permissions container.
- **Know what survives replacement.** A startup snapshot is not continuous backup. Later admin imports need a seed entry or a later offline snapshot; uncommitted/local-only changes need capture before losing the working volume. Never archive a live workspace or write the archive inside it.
- **Keep identity and data separate.** The instance credential opens the tunnel. Repository credentials authorize private clones; an optional cloud API key authorizes cloud embeddings. Inject secrets through the platform, not committed configuration or Terraform raw-value inputs. The serving process runs as uid 1654 and needs appropriate volume and secret permissions.
- **Run one replica per credential.** Use stop-then-start replacement. Two live replicas with the same identity disrupt each other. Size memory for the whole process: uplinks retain loaded trigrams by default, and DuckDB's budget is only part of total memory.

Use the owner's agreed placement, repository access, and resource budget. Ask only for missing choices that matter; do not repeat approvals already given. Prepare the configuration and a reviewable plan before any newly required deployment approval. Consult the platform recipe instead of inventing network placement, secret names, or permissions to unblock an apply.

## Verify the requested outcome

For an investigation, verify that the selected uplink contains the intended repository and that the evidence comes from it. Treat incomplete coverage as a limit on the answer, not evidence of absence.

For a deployment, follow preparation logs and its exit status, then use `rql uplink validate MEMBER_DOOR_URL` with the exact returned URL. Complete a read or query against an intended repository through the discovered name. Tunnel admission, a public HTTP challenge, or an infrastructure resource marked healthy does not by itself prove agents can use the index. Report the checks actually completed and any remaining authentication or indexing boundary.
