[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Filter = "",
    [switch]$NoBuild = $false
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "=== Remote Buddy Windows Test Script ===" -ForegroundColor Cyan

# 1. Locate dotnet.exe automatically
$machinePath = [Environment]::GetEnvironmentVariable("Path", "Machine")
$userPath = [Environment]::GetEnvironmentVariable("Path", "User")
if ($machinePath -or $userPath) {
    $env:PATH = "$userPath;$machinePath;$env:PATH"
}

$dotnetCmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    $candidates = @(
        "C:\Program Files\dotnet\dotnet.exe",
        "C:\Program Files (x86)\dotnet\dotnet.exe",
        "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe",
        "$env:USERPROFILE\.dotnet\dotnet.exe"
    )
    foreach ($cand in $candidates) {
        if (Test-Path $cand) {
            $dir = Split-Path -Parent $cand
            $env:PATH = "$dir;$env:PATH"
            Write-Host "Found dotnet at: $cand (Added to PATH)" -ForegroundColor Green
            break
        }
    }
}

$dotnetCmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Error "Could not find 'dotnet.exe'. Please install .NET 10 SDK or add its directory to PATH."
    exit 1
}

$dotnetVer = & dotnet --version
Write-Host "Using .NET SDK version: $dotnetVer" -ForegroundColor Green

# 2. Build if required
if (-not $NoBuild) {
    Write-Host "`n=== Building Solution ($Configuration) ===" -ForegroundColor Cyan
    & dotnet build RemoteBuddy.sln -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
} else {
    Write-Host "`n[WARN] -NoBuild specified: running tests against existing $Configuration build output." -ForegroundColor Yellow
}

# 3. Run unit tests
Write-Host "`n=== Running Unit Tests ($Configuration) ===" -ForegroundColor Cyan
$testArgs = @("test", "RemoteBuddy.sln", "-c", $Configuration, "--no-build", "--verbosity", "normal")
if ($Filter) {
    $testArgs += @("--filter", $Filter)
}

& dotnet @testArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[FAILED] Tests failed!" -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "`n[SUCCESS] All unit tests passed!" -ForegroundColor Green
