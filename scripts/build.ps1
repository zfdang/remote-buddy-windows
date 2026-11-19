[CmdletBinding()]
param(
    [switch]$Publish = $true,
    [string]$OutputDir = "publish/latest-test"
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

Write-Host "=== Remote Buddy Windows Build Script ===" -ForegroundColor Cyan

# 1. Locate dotnet.exe automatically
$dotnetCmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    $candidates = @(
        "C:\Users\Zhengfa Dang\Documents\Codex\2026-11-02\https-github-com-zfdang-voice-input\work\dotnet\dotnet.exe",
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
    Write-Error "Could not find 'dotnet.exe'. Please install .NET 10 SDK (e.g. 'winget install Microsoft.DotNet.SDK.10') or add its directory to PATH."
    exit 1
}

$dotnetVer = & dotnet --version
Write-Host "Using .NET SDK version: $dotnetVer" -ForegroundColor Green

# 2. Restore solution
Write-Host "`n=== Restoring Solution ===" -ForegroundColor Cyan
& dotnet restore RemoteBuddy.sln
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 3. Build solution
Write-Host "`n=== Building Solution (Release) ===" -ForegroundColor Cyan
& dotnet build RemoteBuddy.sln -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 4. Run tests
Write-Host "`n=== Running Unit Tests ===" -ForegroundColor Cyan
& dotnet test RemoteBuddy.sln -c Release --no-build --verbosity normal
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 5. Publish self-contained executable
if ($Publish) {
    Write-Host "`n=== Publishing Self-Contained Binary to $OutputDir ===" -ForegroundColor Cyan
    & dotnet publish src/RemoteBuddy.App/RemoteBuddy.App.csproj `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $OutputDir
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    $targetExe = Join-Path $OutputDir "RemoteBuddy.App.exe"
    Write-Host "`n[SUCCESS] Build and publish completed successfully!" -ForegroundColor Green
    Write-Host "Executable binary: $targetExe" -ForegroundColor Yellow
} else {
    Write-Host "`n[SUCCESS] Solution build and test completed successfully!" -ForegroundColor Green
}
