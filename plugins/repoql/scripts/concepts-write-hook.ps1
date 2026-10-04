# Surface the concepts relevant to a file before Claude Code edits it, under
# PowerShell. The host owns relevance matching, ranking, and once-per-session
# suppression. Hook failures report to stderr but must never block an edit.
# concepts-write-hook.sh is the same hook for bash.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

$entries = @()
try {
    $payload = Read-HookInput
    $session = $payload.session_id
    $workspace = $payload.cwd
    if (Test-Directory $workspace) { Set-Location -LiteralPath $workspace }

    if (-not (Get-Command rql)) {
        Write-HookWarning 'RepoQL concept hints: rql is unavailable; continuing the edit.'
    } elseif ($session) {
        # Claude Write/Edit uses file_path; multi-file adapters may supply edits[].
        $toolInput = $payload.tool_input
        $candidates = @($toolInput.file_path, $toolInput.path)
        foreach ($edit in @($toolInput.edits)) { $candidates += @($edit.file_path, $edit.path) }
        $files = @($candidates | Where-Object { $_ -is [string] -and $_ } | Sort-Object -Unique | Select-Object -First 8)

        $remaining = 5
        foreach ($file in $files) {
            # Query paths separately so newly created files need not exist in the index.
            $hints = $null | rql concept hints (ConvertTo-LiteralTarget $file) --session $session --limit $remaining --json 2>$null
            if ($LASTEXITCODE -ne 0) {
                Write-HookWarning 'RepoQL concept hints: CLI failed; continuing the edit.'
                continue
            }
            $answer = ConvertFrom-HookJson (@($hints) -join "`n")
            if ($null -eq $answer -or $answer.concepts -isnot [array]) {
                Write-HookWarning 'RepoQL concept hints: invalid CLI response; continuing the edit.'
                continue
            }
            $concepts = @($answer.concepts | Where-Object { $_.uri })
            if ($concepts.Count -eq 0) { continue }
            foreach ($concept in $concepts) {
                $entry = $concept.uri + "`t" + $concept.invariant
                if ($concept.why) { $entry += "`n  why: " + $concept.why }
                $entries += $entry
            }
            $remaining -= $concepts.Count
            if ($remaining -le 0) { break }
        }
    }
} catch {
    Write-HookWarning 'RepoQL concept hints: hook failed; continuing the edit.'
}

if ($entries.Count -gt 0) { Write-HookContext 'PreToolUse' ($entries -join "`n`n") }
exit 0
