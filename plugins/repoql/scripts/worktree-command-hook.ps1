# After a Bash, Grep or Glob call, tell the agent the RepoQL address of a git worktree the call created or searched.
# rql reads the command and decides everything; this adapter forwards it, writing a Grep call as the ripgrep search it runs
# and a Glob call as a find -path search, a deliberate stand-in rql declines to translate, so the agent gets the notice without a call.
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

    # A Grep call is the ripgrep search it runs: the fields that decide which lines
    # match, and the output mode and context lines that decide what it prints, since
    # rql translates a search only when its call returns every printed line.
    # head_limit is a result limit and the -n field only presentation, so both are
    # left out; a content search always numbers its lines.
    # A Glob call is a find over whole paths, which rql leaves untranslated.
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
            $mode = $toolInput.output_mode
            if ($mode -ceq 'content') {
                $command = 'rg -n'
                foreach ($flag in '-A', '-B', '-C') {
                    $lines = $toolInput.$flag
                    if ($null -ne $lines -and [string]$lines -cmatch '\A[0-9]+\z') { $command += " $flag $lines" }
                }
            } elseif ($mode -ceq 'count') {
                $command = 'rg -c'
            } else {
                $command = 'rg -l'
            }
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
