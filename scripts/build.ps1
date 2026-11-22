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
# Refresh PATH from registry in case dotnet was installed in another session
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
$hasSdk = $false
if ($dotnetCmd) {
    $sdks = & dotnet --list-sdks 2>$null
    if ($sdks) { $hasSdk = $true }
}

if (-not $hasSdk) {
    Write-Host "No .NET SDK found. Attempting automatic installation of .NET 10 SDK..." -ForegroundColor Yellow
    $installScript = "$env:TEMP\dotnet-install.ps1"
    if (-not (Test-Path $installScript)) {
        Invoke-WebRequest -Uri "https://dot.net/v1/dotnet-install.ps1" -OutFile $installScript
    }
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File $installScript -Channel 10.0 -InstallDir "$env:USERPROFILE\.dotnet"
    $env:PATH = "$env:USERPROFILE\.dotnet;$env:PATH"
}

$dotnetCmd = Get-Command "dotnet" -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Error "Could not find 'dotnet.exe'. Please install .NET 10 SDK (e.g. 'winget install Microsoft.DotNet.SDK.10') or add its directory to PATH."
    exit 1
}

$dotnetVer = & dotnet --version
Write-Host "Using .NET SDK version: $dotnetVer" -ForegroundColor Green

# 2. Cleanup lingering processes that might lock build outputs
Get-Process -Name "RemoteBuddy.App", "testhost*" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 3. Restore solution
Write-Host "`n=== Restoring Solution ===" -ForegroundColor Cyan
& dotnet restore RemoteBuddy.sln
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 4. Build solution
Write-Host "`n=== Building Solution (Release) ===" -ForegroundColor Cyan
& dotnet build RemoteBuddy.sln -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# 5. Publish self-contained executable
if ($Publish) {
    Write-Host "`n=== Publishing Self-Contained Binary to $OutputDir ===" -ForegroundColor Cyan
    $runningApp = Get-Process -Name "RemoteBuddy.App" -ErrorAction SilentlyContinue
    if ($runningApp) {
        Write-Host "Found running RemoteBuddy.App process (PID $($runningApp.Id)). Stopping it before publishing..." -ForegroundColor Yellow
        $runningApp | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }

    $targetExePath = Join-Path $OutputDir "RemoteBuddy.App.exe"
    if (Test-Path $targetExePath) {
        try {
            Remove-Item $targetExePath -Force -ErrorAction Stop
        } catch {
            # In Windows, a running or locked exe cannot be deleted, but can be renamed out of the way
            $oldExe = Join-Path $OutputDir ("RemoteBuddy.App.old." + [Guid]::NewGuid().ToString("N").Substring(0, 8) + ".exe")
            Move-Item $targetExePath $oldExe -Force -ErrorAction SilentlyContinue
        }
    }

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
    Write-Host "`n[SUCCESS] Solution build completed successfully!" -ForegroundColor Green
}
