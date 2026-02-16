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

Write-Host "Building dashboard..." -ForegroundColor Cyan
Push-Location "$repoRoot/dashboard"
npm install --silent 2>&1 | Out-Null
npx --yes tsc -b
npx --yes vite build
Pop-Location

Write-Host "Publishing RepoQL.ConsoleApp ($config)..." -ForegroundColor Cyan
dotnet publish "$repoRoot/src/RepoQL.ConsoleApp/RepoQL.ConsoleApp.csproj" -c $config -r win-x64 --nologo -v q
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Stopping running repoql processes..." -ForegroundColor Cyan
Get-Process -Name "repoql" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

$source = "$repoRoot/artifacts/publish/RepoQL.ConsoleApp/${configLower}_win-x64"
$dest = "$repoRoot/artifacts/publish"

Write-Host "Copying from $source to $dest..." -ForegroundColor Cyan

# Copy files from nested publish to flat artifacts/publish (excluding the nested folder itself)
Get-ChildItem -Path $source | ForEach-Object {
    Copy-Item $_.FullName -Destination $dest -Recurse -Force
}

Write-Host "Deploy complete!" -ForegroundColor Green
