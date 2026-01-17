<#
.SYNOPSIS
    Updates version numbers across the InfiniteGPU project.

.DESCRIPTION
    This script updates version numbers in:
    - frontend/package.json (version and requiredDesktopVersion)
    - desktop/Scalerize.InfiniteGpu.Desktop/Scalerize.InfiniteGpu.Desktop/Scalerize.InfiniteGpu.Desktop.csproj
    - desktop/Scalerize.InfiniteGpu.Desktop/Scalerize.InfiniteGpu.Desktop.Installer/Product.wxs
    - desktop/Scalerize.InfiniteGpu.Desktop/Scalerize.InfiniteGpu.Desktop.Installer/Package.wxs
    - frontend/src/shared/components/UpdatePrompt.tsx

.PARAMETER Version
    The new version number in format X.X.X.X (e.g., 1.0.14.0)
    For package.json, the version will be converted to X.X.X format

.EXAMPLE
    .\update-version.ps1 -Version 1.0.14.0

.EXAMPLE
    .\update-version.ps1 1.0.14.0
#>

param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version
)

$ErrorActionPreference = "Stop"

# Ensure version has 4 parts for consistency
$versionParts = $Version -split '\.'
if ($versionParts.Count -eq 3) {
    $Version = "$Version.0"
}

# Convert to 3-part version for package.json (npm standard)
$npmVersion = ($Version -split '\.')[0..2] -join '.'

Write-Host "Updating versions to: $Version (npm: $npmVersion)" -ForegroundColor Cyan
Write-Host ""

$scriptRoot = $PSScriptRoot
if (-not $scriptRoot) {
    $scriptRoot = Get-Location
}

# File paths relative to script root
$files = @{
    PackageJson = Join-Path $scriptRoot "frontend\package.json"
    CsProj = Join-Path $scriptRoot "desktop\Scalerize.InfiniteGpu.Desktop\Scalerize.InfiniteGpu.Desktop\Scalerize.InfiniteGpu.Desktop.csproj"
    ProductWxs = Join-Path $scriptRoot "desktop\Scalerize.InfiniteGpu.Desktop\Scalerize.InfiniteGpu.Desktop.Installer\Product.wxs"
    PackageWxs = Join-Path $scriptRoot "desktop\Scalerize.InfiniteGpu.Desktop\Scalerize.InfiniteGpu.Desktop.Installer\Package.wxs"
    UpdatePrompt = Join-Path $scriptRoot "frontend\src\shared\components\UpdatePrompt.tsx"
}

# Verify all files exist
foreach ($key in $files.Keys) {
    if (-not (Test-Path $files[$key])) {
        Write-Error "File not found: $($files[$key])"
        exit 1
    }
}

function Update-JsonVersion {
    param(
        [string]$FilePath,
        [string]$NpmVersion,
        [string]$DesktopVersion
    )
    
    Write-Host "Updating package.json..." -ForegroundColor Yellow
    
    $content = Get-Content $FilePath -Raw
    $json = $content | ConvertFrom-Json
    
    $oldVersion = $json.version
    $oldDesktopVersion = $json.requiredDesktopVersion
    
    $json.version = $NpmVersion
    $json.requiredDesktopVersion = $DesktopVersion
    
    $json | ConvertTo-Json -Depth 10 | Set-Content $FilePath -Encoding UTF8 -NoNewline
    
    Write-Host "  version: $oldVersion -> $NpmVersion" -ForegroundColor Green
    Write-Host "  requiredDesktopVersion: $oldDesktopVersion -> $DesktopVersion" -ForegroundColor Green
}

function Update-CsProj {
    param(
        [string]$FilePath,
        [string]$Version
    )
    
    Write-Host "Updating .csproj..." -ForegroundColor Yellow
    
    $content = Get-Content $FilePath -Raw -Encoding UTF8
    
    # Update Version
    $content = $content -replace '(<Version>)[\d\.]+(<\/Version>)', "`${1}$Version`${2}"
    
    # Update AssemblyVersion
    $content = $content -replace '(<AssemblyVersion>)[\d\.]+(<\/AssemblyVersion>)', "`${1}$Version`${2}"
    
    # Update FileVersion
    $content = $content -replace '(<FileVersion>)[\d\.]+(<\/FileVersion>)', "`${1}$Version`${2}"
    
    Set-Content $FilePath -Value $content -Encoding UTF8 -NoNewline
    
    Write-Host "  Version, AssemblyVersion, FileVersion -> $Version" -ForegroundColor Green
}

function Update-WixProduct {
    param(
        [string]$FilePath,
        [string]$Version
    )
    
    Write-Host "Updating Product.wxs..." -ForegroundColor Yellow
    
    $content = Get-Content $FilePath -Raw -Encoding UTF8
    
    # Update ProductVersion define
    $content = $content -replace '(\<\?define ProductVersion = ")[\d\.]+(" \?\>)', "`${1}$Version`${2}"
    
    Set-Content $FilePath -Value $content -Encoding UTF8 -NoNewline
    
    Write-Host "  ProductVersion -> $Version" -ForegroundColor Green
}

function Update-WixPackage {
    param(
        [string]$FilePath,
        [string]$Version
    )
    
    Write-Host "Updating Package.wxs..." -ForegroundColor Yellow
    
    $content = Get-Content $FilePath -Raw -Encoding UTF8
    
    # Update Version attribute on Package element (match whitespace before Version to avoid matching InstallerVersion)
    $content = $content -replace '(\s+Version=")[\d\.]+(")', "`${1}$Version`${2}"
    
    Set-Content $FilePath -Value $content -Encoding UTF8 -NoNewline
    
    Write-Host "  Version -> $Version" -ForegroundColor Green
}

function Update-UpdatePrompt {
    param(
        [string]$FilePath,
        [string]$Version
    )
    
    Write-Host "Updating UpdatePrompt.tsx..." -ForegroundColor Yellow
    
    $content = Get-Content $FilePath -Raw -Encoding UTF8
    
    # Update REQUIRED_DESKTOP_VERSION constant
    $content = $content -replace "(const REQUIRED_DESKTOP_VERSION = ')[\d\.]+(')", "`${1}$Version`${2}"
    
    Set-Content $FilePath -Value $content -Encoding UTF8 -NoNewline
    
    Write-Host "  REQUIRED_DESKTOP_VERSION -> $Version" -ForegroundColor Green
}

try {
    # Update all files
    Update-JsonVersion -FilePath $files.PackageJson -NpmVersion $npmVersion -DesktopVersion $Version
    Update-CsProj -FilePath $files.CsProj -Version $Version
    Update-WixProduct -FilePath $files.ProductWxs -Version $Version
    Update-WixPackage -FilePath $files.PackageWxs -Version $Version
    Update-UpdatePrompt -FilePath $files.UpdatePrompt -Version $Version
    
    Write-Host ""
    Write-Host "Version update complete!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Updated files:" -ForegroundColor Cyan
    foreach ($key in $files.Keys) {
        Write-Host "  - $($files[$key])" -ForegroundColor Gray
    }
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Cyan
    Write-Host "  1. Review the changes with 'git diff'" -ForegroundColor Gray
    Write-Host "  2. Build and test the application" -ForegroundColor Gray
    Write-Host "  3. Commit the version bump" -ForegroundColor Gray
}
catch {
    Write-Error "Failed to update versions: $_"
    exit 1
}
