# After a Bash, Grep or Glob call, tell the agent the RepoQL address of a git worktree the call created or searched.
# rql reads the command and decides everything; this adapter forwards it, writing a Grep or Glob call as the shell search it equals.
# worktree-command-hook.sh is the same hook for bash.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

# One POSIX shell word: bare when it holds only characters no shell treats
# specially, otherwise single-quoted, with each single quote written as '\''.
function ConvertTo-ShellWord([string]$value) {
    if ($value -cmatch '\A[A-Za-z0-9_./:=@%+,-]+\z') { return $value }
    return "'" + $value.Replace("'", "'\''") + "'"
}

try {
    $payload = Read-HookInput
    $session = $payload.session_id
    $agent = $payload.agent_id
    $workspace = $payload.cwd
    $reading = $env:CLAUDE_PROJECT_DIR
    if (-not $session -or -not (Test-Directory $reading) -or -not (Get-Command rql)) { exit 0 }

    # A Grep call is the ripgrep search it runs, with the fields that decide which
    # lines match; output_mode, -n, -A, -B, -C and head_limit change only what is
    # printed. A Glob call is a find over whole paths, which rql leaves untranslated.
    # Both search their path, or the session's directory without one.
    $toolInput = $payload.tool_input
    $tool = [string]$payload.tool_name
    $command = $null
    if ($tool -ceq 'Bash') {
        $command = $toolInput.command
    } elseif ($tool -ceq 'Grep' -or $tool -ceq 'Glob') {
        $pattern = $toolInput.pattern
        $path = $toolInput.path
        if ($path -isnot [string] -or -not $path) { $path = $workspace }
        if ($pattern -isnot [string] -or -not $pattern -or $path -isnot [string] -or -not $path) { exit 0 }
        if ($tool -ceq 'Grep') {
            $command = 'rg -n'
            if ($toolInput.'-i' -eq $true) { $command += ' -i' }
            $glob = $toolInput.glob
            if ($glob -is [string] -and $glob) { $command += ' -g ' + (ConvertTo-ShellWord $glob) }
            $type = $toolInput.type
            if ($type -is [string] -and $type) { $command += ' -t ' + (ConvertTo-ShellWord $type) }
            if ($toolInput.multiline -eq $true) { $command += ' -U --multiline-dotall' }
            $command += ' -e ' + (ConvertTo-ShellWord $pattern) + ' -- ' + (ConvertTo-ShellWord $path)
        } else {
            if ($pattern.StartsWith('/')) { $whole = $pattern }
            elseif ($path.EndsWith('/')) { $whole = $path + $pattern }
            else { $whole = $path + '/' + $pattern }
            $command = 'find ' + (ConvertTo-ShellWord $path) + ' -path ' + (ConvertTo-ShellWord $whole)
        }
    }
    if ($command -isnot [string] -or -not $command) { exit 0 }

    Set-Location -LiteralPath $reading
    $env:REPOQL_CWD = $reading
    $arguments = @('worktree', 'check', '--command', $command, '--session', $session)
    if ($agent) { $arguments += @('--agent', $agent) }
    if (Test-Directory $workspace) { $arguments += @('--cwd', $workspace) }
    $notice = Invoke-Rql '' $arguments
    if ($RqlExitCode -ne 0) { exit 0 }
    if ($notice) { Write-HookContext 'PostToolUse' $notice }
} catch { }
exit 0
