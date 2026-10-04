# Forward delivered text only, on Windows PowerShell. The host owns matching,
# scope, and session suppression. vocabulary-read-hook.sh is the same hook for bash.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

try {
    $payload = Read-HookInput
    $session = $payload.session_id
    $workspace = $payload.cwd
    if (-not $session -or -not (Test-Directory $workspace)) { exit 0 }

    # Do not scan tool arguments, shell commands, images, or failed tool responses.
    $tool = [string]$payload.tool_name
    if ($tool -cnotmatch '^(Read|read_file|mcp__.*[Rr][Ee][Pp][Oo][Qq][Ll].*__read)$') { exit 0 }
    $response = $payload.tool_response
    $isObject = $null -ne $response -and $response -isnot [string] -and $response -isnot [array]
    if ($isObject -and ($response.isError -eq $true -or $response.is_error -eq $true)) { exit 0 }

    $toolInput = $payload.tool_input
    $target = [string](@($toolInput.file_path, $toolInput.path, $toolInput.uriGlob, $toolInput.uri) |
        Where-Object { $_ -is [string] -and $_ } | Select-Object -First 1)
    $target = ($target -split ' =>', 2)[0]
    $target = ($target -split '#', 2)[0]
    if (-not $target) { exit 0 }
    # Native relative paths resolve from the harness cwd and are literal files;
    # MCP globs are repository-relative and already globs.
    if ($tool -ceq 'Read' -or $tool -ceq 'read_file') {
        if ($target -notmatch '^(/|[A-Za-z]:|.*://)') { $target = "$workspace/$target" }
        $target = ConvertTo-LiteralTarget $target
    }

    # A response is the text itself, a native read's file body, or MCP text blocks.
    $text = ''
    if ($response -is [string]) { $text = $response }
    elseif ($isObject -and $response.file.content -is [string]) { $text = $response.file.content }
    elseif ($isObject -and $response.content -is [string]) { $text = $response.content }
    elseif ($isObject -and $response.content -is [array]) { $text = Get-TextBlocks $response.content }
    $text = Limit-HookText $text
    if (-not $text) { exit 0 }

    if (-not (Get-Command rql)) {
        Write-HookWarning 'RepoQL vocabulary hints: rql is unavailable; continuing the read.'
        exit 0
    }
    Set-Location -LiteralPath $workspace
    $env:REPOQL_CWD = $workspace
    $hints = $text | rql vocabulary hints $target --session $session --limit 5 --max-chars 2000 2>$null
    if ($LASTEXITCODE -ne 0) {
        Write-HookWarning 'RepoQL vocabulary hints: CLI failed; continuing the read.'
        exit 0
    }
    $hints = (@($hints) -join "`n").Trim()
    if ($hints) { Write-HookContext 'PostToolUse' $hints }
} catch {
    Write-HookWarning 'RepoQL vocabulary hints: hook failed; continuing the read.'
}
exit 0
