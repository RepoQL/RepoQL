@echo off
REM RepoQL MCP launcher for Windows - what .mcp.json starts. rql-mcp is the same
REM launcher for macOS and Linux, and explains why the server is not a bare
REM `rql` command.
REM
REM The installer's PATH change reaches only newly started terminals, so this
REM looks in the canonical install directory first. stdout is the JSON-RPC
REM stream: nothing here may write to it before rql does.
setlocal
set "RQL=%LOCALAPPDATA%\rql\rql.exe"
if exist "%RQL%" goto run
for /f "delims=" %%I in ('where rql 2^>nul') do (
    set "RQL=%%I"
    goto run
)
if "%REPOQL_NO_BOOTSTRAP%"=="1" goto missing
echo [repoql] rql is not installed - downloading it 1>&2
powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0bootstrap-rql.ps1" <nul >nul 2>nul
set "RQL=%LOCALAPPDATA%\rql\rql.exe"
if exist "%RQL%" goto run

:missing
echo [repoql] rql is unavailable. Install it from PowerShell with: irm https://downloads.repoql.ai/latest/install-rql.ps1 ^| iex 1>&2
exit /b 1

:run
"%RQL%" mcp %*
exit /b %ERRORLEVEL%
