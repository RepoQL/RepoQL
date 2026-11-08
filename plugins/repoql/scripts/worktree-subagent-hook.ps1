# When a subagent starts, tell it the RepoQL address of each git worktree its session works in.
# rql owns the session's worktrees, the mounts, and which addresses it names; this adapter forwards the ids.
# worktree-subagent-hook.sh is the same hook for bash.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

try {
    $payload = Read-HookInput
    $session = $payload.session_id
    $agent = $payload.agent_id
    $reading = $env:CLAUDE_PROJECT_DIR
    if (-not $session -or -not (Test-Directory $reading) -or -not (Get-Command rql)) { exit 0 }

    Set-Location -LiteralPath $reading
    $env:REPOQL_CWD = $reading
    $arguments = @('worktree', 'check', '--subagent', '--session', $session)
    if ($agent) { $arguments += @('--agent', $agent) }
    $notice = Invoke-Rql '' $arguments
    if ($RqlExitCode -ne 0) { exit 0 }
    if ($notice) { Write-HookContext 'SubagentStart' $notice }
} catch { }
exit 0
