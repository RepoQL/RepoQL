# Bitbucket Source Links

## Bitbucket Cloud

**Structure**: `https://bitbucket.org/{workspace}/{repo}/src/{commit}/{path}#{filename}-{line}`

Line anchor includes filename: `#auth.js-42`

## Bitbucket Server/Data Center

**Structure**: `https://{host}/projects/{project}/repos/{repo}/browse/{path}?at={ref}#{line}`

Note: `?at=` must come before `#`.

User repos: `/users/{user}/repos/` instead of `/projects/{project}/repos/`

> [Atlassian Docs](https://support.atlassian.com/bitbucket-cloud/docs/hyperlink-to-source-code-in-bitbucket/)
