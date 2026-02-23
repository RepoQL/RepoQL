---
description: Reference for Bitbucket source link URL formats.
tags: [skill, writing-documents, source-links, bitbucket]
audience: [LLMs]
categories: ["Skill[100%]"]
---

# Bitbucket Source Links

## Bitbucket Cloud

**Structure**: `https://bitbucket.org/{workspace}/{repo}/src/{commit}/{path}#{filename}-{line}`

Line anchor includes filename: `#auth.js-42`

## Bitbucket Server/Data Center

**Structure**: `https://{host}/projects/{project}/repos/{repo}/browse/{path}?at={ref}#{line}`

Note: `?at=` must come before `#`.

User repos: `/users/{user}/repos/` instead of `/projects/{project}/repos/`

> [Atlassian Docs](https://support.atlassian.com/bitbucket-cloud/docs/hyperlink-to-source-code-in-bitbucket/)
