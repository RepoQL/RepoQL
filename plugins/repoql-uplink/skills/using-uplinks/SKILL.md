---
name: using-uplinks
description: "Query your organization's RepoQL uplinks — shared, remotely hosted code indexes — through this plugin's remote connection. Use before the first RepoQL call of a session, when choosing which uplink holds a repository, when a call fails for a missing default, an unknown or offline uplink, or lost access, and when the RepoQL tools are absent or need sign-in. Also use when the user mentions an uplink, a team index, or code that is not checked out here."
---

# Use your organization's uplinks

This plugin connects to one remote endpoint that reaches every uplink your account can access. Nothing runs on this machine: there is no local index, and the working directory is not searched.

This file is complete on purpose. It has to work when the connection does not.

## Start with the destination

An uplink is an index, not a repository. One uplink holds many repositories.

```
uplinks()
```

The result names the organization, the default chosen at sign-in, and each accessible uplink with `connected` or `offline`. Use the names exactly as returned.

Then find what the chosen uplink holds, and scope by the URIs it returns:

```
query(sql="SELECT source_uri, file_count FROM Filesystems ORDER BY source_uri", uplink="acme/platform")
explore(uriGlob="github://acme/api/**", keywords="authentication", question="Where are access tokens checked?", uplink="acme/platform")
```

`acme/platform` and `github://acme/api` are placeholders for discovered values. `file:///` addresses the uplink host's own workspace, never this machine.

## Route every call deliberately

- Omit `uplink` to use the sign-in default. With no default, the call fails and says so.
- Pass `uplink` to send one call elsewhere. It changes that call only; nothing is remembered.
- Keep the same `uplink` on follow-up calls. Concurrent calls do not affect each other.
- One call searches one uplink. Comparing two uplinks takes one call to each; keep the uplink and repository attached to each result when you combine them.
- Ask the user when the intended uplink is unclear. Do not guess between indexes.

## What this connection can do

Read tools only: `explore`, `read`, `query`, `explain`, `discover_vocabulary`, and `uplinks`. Importing repositories, capturing concepts, running `execute`, and host commands need a local RepoQL install or the uplink's admin connection.

The index reflects pushed code. The user's uncommitted edits are not in it, and a recent push may still be indexing. When freshness matters, check the source revision before concluding code is absent.

Every other skill in this plugin loads its guidance from `help:///` with `read`. That read is routed like any other: with no default, add `uplink="<name>"` to it.

## When something is wrong

| What you see | What to do |
|---|---|
| No RepoQL tools, or the server needs authentication | The user runs `/mcp`, selects the RepoQL server, and signs in through the browser. You cannot start that flow. |
| The server fails to connect | The endpoint may not be the one the user's RepoQL service uses. `REPOQL_UPLINK_URL` overrides it with a shared URL ending in `/uplink`; set it before starting Claude Code. |
| No default uplink was selected | Call `uplinks()`, then pass a name. To set a default, the user signs in again through `/mcp`. |
| Unknown uplink | Call `uplinks()` and use a returned name. Another organization's uplink needs a new sign-in. |
| No uplinks, or one is missing | The signed-in account or organization is wrong, or access was not granted. The organization's RepoQL admin grants it. |
| An uplink is offline | Its operator restores it. Signing in again does not. |
| A repository is missing or a search is empty | List `Filesystems` on that uplink and check indexing before concluding the code is absent. |
| A tool or parameter is unsupported on an uplink | That uplink's host runs an older RepoQL. Its operator updates it. Never retry the call against a different uplink as a substitute. |
| Access denied or revoked | The organization admin reviews access. Sign in again if the error asks for it. |

## Setting up or operating an uplink

Deployment, storage, and access guidance ships with the host. Load it from an uplink:

```
read("help:///skills/using-uplinks/SKILL.md => content", 5000)
```
