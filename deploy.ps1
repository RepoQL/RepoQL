#!/usr/bin/env pwsh
# Deploy script: publish, kill old processes, copy to artifacts

param(
    [switch]$Release
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot

# Default to Debug for selftest tool access, use -Release for production
$config = if ($Release) { "Release" } else { "Debug" }
$configLower = $config.ToLower()

Write-Host "Publishing RepoQL.ConsoleApp ($config)..." -ForegroundColor Cyan
dotnet publish "$repoRoot/src/RepoQL.ConsoleApp/RepoQL.ConsoleApp.csproj" -c $config -r win-x64 --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Publishing RepoQL.McpProxy ($config)..." -ForegroundColor Cyan
dotnet publish "$repoRoot/src/RepoQL.McpProxy/RepoQL.McpProxy.csproj" -c $config -r win-x64 --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "Proxy build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Stopping running repoql processes..." -ForegroundColor Cyan
Get-Process -Name "repoql" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "RepoQL.McpProxy" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$source = "$repoRoot/artifacts/publish/RepoQL.ConsoleApp/${configLower}_win-x64"
$dest = "$repoRoot/artifacts/publish"

Write-Host "Copying from $source to $dest..." -ForegroundColor Cyan

# Copy files from nested publish to flat artifacts/publish (excluding the nested folder itself)
Get-ChildItem -Path $source | ForEach-Object {
    Copy-Item $_.FullName -Destination $dest -Recurse -Force
}

# Copy proxy (single file)
$proxyExe = "$repoRoot/artifacts/publish/RepoQL.McpProxy/${configLower}_win-x64/RepoQL.McpProxy.exe"
if (Test-Path $proxyExe) {
    Copy-Item $proxyExe -Destination $dest -Force
}

Write-Host "Deploy complete!" -ForegroundColor Green
