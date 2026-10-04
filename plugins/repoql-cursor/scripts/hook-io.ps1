# Shared plumbing for the Cursor hooks on Windows - dot-sourced, never run.
#
# Cursor reads one JSON object from stdout and counts stderr as a failure, so a
# hook never lets an error reach the console and always answers through
# Write-HookOutput. Both directions are UTF-8 bytes on the raw streams: the
# console code page would otherwise garble any path or concept outside ASCII.
#
# These scripts run under Windows PowerShell 5.1, which reads a script without a
# byte-order mark as ANSI. Keep every .ps1 here pure ASCII.
$ErrorActionPreference = 'SilentlyContinue'
$utf8 = New-Object System.Text.UTF8Encoding $false
# Decode what rql prints as UTF-8 too.
try { [Console]::OutputEncoding = $utf8 } catch { }

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

function Test-Directory($path) {
    return [bool]$path -and (Test-Path -LiteralPath $path -PathType Container)
}
