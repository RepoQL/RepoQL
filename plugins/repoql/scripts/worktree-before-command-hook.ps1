# Before a Bash call that may create a git worktree, let rql list the worktrees that exist, so the call's new one can be found after it.
# rql owns the listing and the comparison; this adapter's one test is whether the command mentions `worktree add`.
# It prints nothing and never blocks the call. worktree-before-command-hook.sh is the same hook for bash.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

try {
    $payload = Read-HookInput
    $command = $payload.tool_input.command
    # Without this test every Bash call would start rql before it runs.
    if ($command -isnot [string] -or -not $command.Contains('worktree add')) { exit 0 }
    $session = $payload.session_id
    $agent = $payload.agent_id
    $reading = $env:CLAUDE_PROJECT_DIR
    if (-not $session -or -not (Test-Directory $reading) -or -not (Get-Command rql)) { exit 0 }

    Set-Location -LiteralPath $reading
    $env:REPOQL_CWD = $reading
    $arguments = @('worktree', 'check', '--before-command', $command, '--session', $session)
    if ($agent) { $arguments += @('--agent', $agent) }
    Invoke-Rql '' $arguments | Out-Null
} catch { }
exit 0
