# Surface the concepts relevant to a file before Cursor's agent writes it, on
# Windows. The host owns relevance matching, ranking, and once-per-session
# suppression. concepts-write-hook.sh is the same hook for macOS and Linux;
# run-hook.cmd picks between them.
#
# preToolUse is a permission hook: Cursor blocks the write on invalid JSON, so
# every path prints exactly one JSON object and never a permission decision -
# {} lets the write through untouched.
. (Join-Path $PSScriptRoot 'hook-io.ps1')

# The host reads targets as URI globs: escape the metacharacters a real path can
# hold, so app/[slug]/page.tsx names that file instead of a character class.
function ConvertTo-LiteralTarget([string]$path) {
    return $path.Replace('*', '%2A').Replace('?', '%3F').Replace('[', '%5B').Replace('{', '%7B').Replace(';', '%3B')
}

$entries = @()
try {
    $payload = Read-HookInput
    $session = $payload.conversation_id
    if (-not $session) { $session = $payload.session_id }

    if ($session -and (Get-Command rql)) {
        $workspace = $payload.cwd
        if (-not $workspace) { $workspace = @($payload.workspace_roots)[0] }
        if (-not (Test-Directory $workspace)) { $workspace = $env:CURSOR_PROJECT_DIR }
        if (Test-Directory $workspace) { Set-Location -LiteralPath $workspace }

        # Cursor does not document the Write tool's input; accept every path field its
        # payloads and the Claude-compatible shape are known to use. tool_input may
        # arrive as a JSON string.
        $toolInput = $payload.tool_input
        if ($toolInput -is [string]) { $toolInput = ConvertFrom-HookJson $toolInput }
        $candidates = @($toolInput.file_path, $toolInput.path, $toolInput.target_file, $toolInput.filePath)
        foreach ($edit in @($toolInput.edits)) { $candidates += @($edit.file_path, $edit.path) }
        $files = @($candidates | Where-Object { $_ -is [string] -and $_ } | Sort-Object -Unique | Select-Object -First 8)

        $remaining = 5
        foreach ($file in $files) {
            # Query paths separately so newly created files need not exist in the index.
            $hints = $null | rql concept hints (ConvertTo-LiteralTarget $file) --session $session --limit $remaining --json 2>$null
            if ($LASTEXITCODE -ne 0) { continue }
            $concepts = @((ConvertFrom-HookJson (@($hints) -join "`n")).concepts | Where-Object { $_.uri })
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
} catch { }

if ($entries.Count -gt 0) { Write-HookOutput @{ additional_context = $entries -join "`n" } } else { Write-HookOutput @{} }
exit 0
