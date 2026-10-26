---
name: writing-connectors
description: "Write, extend, fix or review a RepoQL connector: the two files that copy an outside source into files RepoQL indexes, so its records can be searched and cited beside the code. Use when asked to add a source to RepoQL or make one searchable, for example 'add our Zendesk to RepoQL', 'index our Jira tickets', 'connect the wiki', 'can RepoQL read our Slack', when a source has no connector yet, or when an existing connector's import is stale, incomplete, hard to search, or holds data it should not. Not needed to install or import a connector that already exists, or to import a git repository, a local folder or an S3 bucket, which RepoQL reads without one."
---

# Write a connector

## Load

Read the complete skill from the installed version of `rql` before proceeding:

```
read("help:///skills/writing-connectors/SKILL.md => content", 6000)
```

If the page is not found, the installed `rql` is older than this skill. Run `rql update`, then read it again.
