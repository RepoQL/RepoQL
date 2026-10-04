: << 'CMDBLOCK'
@echo off
REM One hook command for every shell Claude Code uses. PowerShell runs this file
REM as a batch file, which starts the hook's .ps1. A POSIX shell or Git Bash
REM reads the same file as a shell script: the batch half is a here-document,
REM and the line after it starts the hook's .sh. hooks.json dot-sources the
REM file because that one spelling is valid in PowerShell and in a POSIX shell.
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dpn0.ps1"
exit /b %ERRORLEVEL%
CMDBLOCK
exec bash "$CLAUDE_PLUGIN_ROOT/scripts/worktree-check-hook.sh"
