#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Pack the ai-code-review tool and install it globally for local testing.
    Version is timestamp-based so each run produces a unique package.
#>

$ErrorActionPreference = 'Stop'

$project    = "src/CodeReviewAgent/CodeReviewAgent.csproj"
$outDir     = "nupkg"
$packageId  = "rasitha.DevOpsCodeReviewAgent"
$commandName = "ai-code-review"

# Clean output dir so old packages don't accumulate
if (Test-Path $outDir) {
    Remove-Item "$outDir\*" -Recurse -Force
} else {
    New-Item -ItemType Directory -Path $outDir | Out-Null
}

# Timestamp version: 0.0.0-local.yyyyMMdd.HHmmss
# Major 0.0.0 signals "not a real release"; prerelease label keeps it off NuGet search
$version = "0.0.0-local.$(Get-Date -Format 'yyyyMMdd.HHmmss')"
Write-Host ""
Write-Host "Version : $version" -ForegroundColor Cyan

# Build
Write-Host "Building..." -ForegroundColor Cyan
dotnet build $project -c Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Pack (reuse the build output)
Write-Host "Packing..." -ForegroundColor Cyan
dotnet pack $project -c Release --no-build -p:Version=$version -o $outDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

# Uninstall any global tool that already owns the command name, regardless of package ID.
# This handles the case where a previous manual install used a different package ID.
$toolList = dotnet tool list -g 2>&1
$toolList | Where-Object { $_ -match $commandName } | ForEach-Object {
    $existingId = ($_ -split '\s+')[0]
    Write-Host "Uninstalling existing tool '$existingId'..." -ForegroundColor Yellow
    dotnet tool uninstall -g $existingId
}

# Install from the local source
Write-Host "Installing..." -ForegroundColor Cyan
dotnet tool install -g $packageId --add-source $outDir --version $version
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host ""
Write-Host "Done. Run: $commandName --help" -ForegroundColor Green
