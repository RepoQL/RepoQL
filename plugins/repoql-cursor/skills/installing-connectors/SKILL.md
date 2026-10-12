---
name: installing-connectors
description: "Install a connector that already exists and import a source through it, so the source's records can be searched in RepoQL. Use when asked to install, set up or enable a connector, for example 'install the Jira connector', 'set up this .nupkg', 'connect our Zendesk' when a connector for it exists, to add another account of an installed connector, to update or remove one, or when an import is waiting for a credential or has failed. Not for writing or changing a connector: that is the writing-connectors skill."
---

# Install a connector

## Load

Read the complete skill from the installed version of `rql` before proceeding:

```
read("help:///skills/installing-connectors/SKILL.md => content", 6000)
```

If the page is not found, the installed `rql` is older than this skill. Run `rql update`, then read it again.
