---
name: using-uplinks
description: "Use or set up RepoQL uplinks: shared, remotely hosted indexes that agents and CI can query without local clones. Use when the user mentions an uplink, a team or remote RepoQL index, searching repositories available on another host, or deploying RepoQL on AWS, GCP, or Kubernetes. Also use when session context lists an accessible uplink relevant to repositories missing locally. Covers discovery, explicit remote routing, source scope, access, storage, snapshot preparation, and deployment guidance. Ordinary work confined to the local repository does not need this skill."
---

# Use a shared RepoQL index

## Load

Read the complete skill from the installed version of `rql` before proceeding:

```
read("help:///skills/using-uplinks/SKILL.md => content", 5000)
```
