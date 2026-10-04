# After a RepoQL tool call, tell the agent when RepoQL answered from a different git worktree than the one it edits.
# rql owns detection, divergence, and notice suppression; this adapter forwards the call's scope and delivered text.
# worktree-check-hook.sh is the same hook for bash.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

try {
    $payload = Read-HookInput
    $session = $payload.session_id
    $workspace = $payload.cwd
    $reading = $env:CLAUDE_PROJECT_DIR
    if (-not $session -or -not (Test-Directory $reading) -or -not (Get-Command rql)) { exit 0 }
    if ([string]$payload.tool_name -cnotmatch '^mcp__(.*[Rr][Ee][Pp][Oo][Qq][Ll].*|rql)__') { exit 0 }

    $toolInput = $payload.tool_input
    $pattern = @($toolInput.uriGlob, $toolInput.uri_glob, $toolInput.uri) | Where-Object { $_ -is [string] -and $_ } | Select-Object -First 1

    # A response is the text itself, or text blocks under content or at the top.
    $response = $payload.tool_response
    $text = ''
    if ($response -is [string]) { $text = $response }
    elseif ($response -is [array]) { $text = Get-TextBlocks $response }
    elseif ($null -ne $response -and $response.content -is [string]) { $text = $response.content }
    elseif ($null -ne $response -and $response.content -is [array]) { $text = Get-TextBlocks $response.content }
    $text = Limit-HookText $text

    Set-Location -LiteralPath $reading
    $env:REPOQL_CWD = $reading
    $arguments = @('worktree', 'check', '--session', $session)
    if (Test-Directory $workspace) { $arguments += @('--cwd', $workspace) }
    if ($pattern) { $arguments += @('--pattern', $pattern) }
    $notice = $text | rql @arguments 2>$null
    if ($LASTEXITCODE -ne 0) { exit 0 }
    $notice = (@($notice) -join "`n").Trim()
    if ($notice) { Write-HookContext 'PostToolUse' $notice }
} catch { }
exit 0
