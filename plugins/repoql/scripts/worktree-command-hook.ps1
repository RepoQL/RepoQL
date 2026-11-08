# After a Bash call that may have created a git worktree, tell the agent the RepoQL address rql mounted it under.
# rql finds the new worktree and decides the notice; this adapter's one test is whether the command mentions `worktree add`.
# worktree-command-hook.sh is the same hook for bash.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

try {
    $payload = Read-HookInput
    $command = $payload.tool_input.command
    # Without this test every Bash call would start rql after it runs.
    if ($command -isnot [string] -or -not $command.Contains('worktree add')) { exit 0 }
    $session = $payload.session_id
    $agent = $payload.agent_id
    $reading = $env:CLAUDE_PROJECT_DIR
    if (-not $session -or -not (Test-Directory $reading) -or -not (Get-Command rql)) { exit 0 }

    Set-Location -LiteralPath $reading
    $env:REPOQL_CWD = $reading
    $arguments = @('worktree', 'check', '--command', $command, '--session', $session)
    if ($agent) { $arguments += @('--agent', $agent) }
    $notice = Invoke-Rql '' $arguments
    if ($RqlExitCode -ne 0) { exit 0 }
    if ($notice) { Write-HookContext 'PostToolUse' $notice }
} catch { }
exit 0
