# Shared plumbing for the hooks on Windows - dot-sourced, never run.
#
# A harness reads a hook's reply from stdout, so a hook never lets a stray error
# reach the console and always answers through these functions. Both directions
# are UTF-8 bytes on the raw streams: the console code page would otherwise
# garble any path or concept outside ASCII.
#
# These scripts run under Windows PowerShell 5.1, which reads a script without a
# byte-order mark as ANSI. Keep every .ps1 here pure ASCII.
$ErrorActionPreference = 'SilentlyContinue'
$utf8 = New-Object System.Text.UTF8Encoding $false
# Decode what rql prints as UTF-8 too, and send it text the same way.
try { [Console]::OutputEncoding = $utf8 } catch { }
$OutputEncoding = $utf8

# rql installs to %LOCALAPPDATA%\rql, and the hook's PATH was inherited from a
# process that may predate the registry PATH entry.
if ($env:LOCALAPPDATA) {
    $env:PATH = (Join-Path $env:LOCALAPPDATA 'rql') + [IO.Path]::PathSeparator + $env:PATH
}

# Parse JSON text, or return nothing when it is not JSON. Windows PowerShell's
# ConvertFrom-Json refuses text over two million characters, and a write
# payload carries a whole file, so there the serializer is used directly.
function ConvertFrom-HookJson([string]$text) {
    try {
        if ($PSVersionTable.PSVersion.Major -gt 5) { return $text | ConvertFrom-Json }
        Add-Type -AssemblyName System.Web.Extensions
        $serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
        $serializer.MaxJsonLength = [int]::MaxValue
        return $serializer.DeserializeObject($text)
    } catch {
        return $null
    }
}

# Parse the hook payload on stdin. PowerShell may put a byte-order mark ahead of
# the text it pipes to this process.
function Read-HookInput {
    try {
        $buffer = New-Object System.IO.MemoryStream
        [Console]::OpenStandardInput().CopyTo($buffer)
        return ConvertFrom-HookJson $utf8.GetString($buffer.ToArray()).TrimStart([char]0xFEFF)
    } catch {
        return $null
    }
}

function Write-HookOutput($output) {
    $bytes = $utf8.GetBytes((ConvertTo-Json $output -Compress) + "`n")
    $stdout = [Console]::OpenStandardOutput()
    $stdout.Write($bytes, 0, $bytes.Length)
    $stdout.Flush()
}

# Hand text to the model for a hook event, in the hookSpecificOutput shape
# Claude Code and Codex read.
function Write-HookContext([string]$hookEvent, [string]$text) {
    Write-HookOutput @{ hookSpecificOutput = @{ hookEventName = $hookEvent; additionalContext = $text } }
}

function Write-HookWarning([string]$message) {
    [Console]::Error.WriteLine($message)
}

# The text of the blocks whose type is text, one per line.
function Get-TextBlocks($blocks) {
    return (@($blocks) | Where-Object { $_.type -eq 'text' -and $_.text -is [string] } | ForEach-Object { $_.text }) -join "`n"
}

# At most 65536 UTF-16 characters, never half of a surrogate pair: the CLI reads
# up to 131072.
function Limit-HookText([string]$text) {
    if ($text.Length -le 65536) { return $text }
    $length = 65536
    if ([char]::IsHighSurrogate($text[$length - 1])) { $length-- }
    return $text.Substring(0, $length)
}

# The host reads targets as URI globs: escape the metacharacters a real path can
# hold, so app/[slug]/page.tsx names that file instead of a character class.
function ConvertTo-LiteralTarget([string]$path) {
    return $path.Replace('*', '%2A').Replace('?', '%3F').Replace('[', '%5B').Replace('{', '%7B').Replace(';', '%3B')
}

function Test-Directory($path) {
    return [bool]$path -and (Test-Path -LiteralPath $path -PathType Container)
}
