<#
.SYNOPSIS
  Local publish + Inno Setup compile (optional; CI does this on release).
#>
param(
    [string]$Version = "0.2.0"
)

$ErrorActionPreference = "Stop"
$Root = Split-Path $PSScriptRoot -Parent
Set-Location $Root

$parts = $Version.Split('.')
while ($parts.Length -lt 4) { $parts += '0' }
$fileVersion = [string]::Join('.', $parts[0..3])

New-Item -ItemType Directory -Path "artifacts/publish" -Force | Out-Null

dotnet publish AcerInputFix.csproj `
    -c Release `
    -r win-x64 `
    --self-contained true `
    /p:PublishSingleFile=true `
    /p:Version=$Version `
    /p:AssemblyVersion=$fileVersion `
    /p:FileVersion=$fileVersion `
    /p:InformationalVersion=$Version `
    -o artifacts/publish

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 not found. Install from https://jrsoftware.org/isinfo.php"
}

New-Item -ItemType Directory -Path "artifacts/installer" -Force | Out-Null
& $iscc "/DMyAppVersion=$Version" "installer\AcerInputFix.iss"

Write-Host ""
Write-Host "Installer: artifacts\installer\AcerInputFix-$Version-win-x64-setup.exe"
