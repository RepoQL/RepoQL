# Record which git worktree this session edits, so worktree-check-hook.ps1 can say when RepoQL reads another tree.
# rql owns tree resolution and state. worktree-track-hook.sh is the same hook for bash; that one tracks detached,
# and this one tracks inside the hook's own timeout.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

try {
    $payload = Read-HookInput
    $session = $payload.session_id
    $workspace = $payload.cwd
    # The MCP server, and therefore RepoQL's reading tree, starts in the project directory and stays there.
    $reading = $env:CLAUDE_PROJECT_DIR
    if (-not $session -or -not (Test-Directory $reading) -or -not (Get-Command rql)) { exit 0 }

    $toolInput = $payload.tool_input
    $candidates = @($toolInput.file_path, $toolInput.notebook_path, $toolInput.path)
    foreach ($edit in @($toolInput.edits)) { $candidates += @($edit.file_path, $edit.path) }
    $files = @($candidates | Where-Object { $_ -is [string] -and $_ } | Sort-Object -Unique | Select-Object -First 8)

    Set-Location -LiteralPath $reading
    $env:REPOQL_CWD = $reading
    foreach ($file in $files) {
        if ($file -notmatch '^(/|[A-Za-z]:)') {
            if (-not (Test-Directory $workspace)) { continue }
            $file = "$workspace/$file"
        }
        rql worktree track $file --session $session 2>$null | Out-Null
    }
} catch { }
exit 0
