: << 'CMDBLOCK'
@echo off
REM One hook command for every platform. Cursor hands a hook to the user's shell:
REM cmd.exe runs this batch half, a POSIX shell skips it as a here-document and
REM runs the lines after it. Each hook is <name>.ps1 on Windows and <name>.sh
REM elsewhere, so Windows needs neither bash nor a file association for .sh.
REM
REM Cursor reads one JSON object from stdout and counts stderr as a failure, so
REM a PowerShell that cannot start still answers {}.
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0%~1.ps1" 2>nul || echo {}
exit /b 0
CMDBLOCK
exec bash "${0%/*}/$1.sh"
