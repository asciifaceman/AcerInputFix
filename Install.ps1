<#
.SYNOPSIS
  Install or upgrade AcerInputFix.

.DESCRIPTION
  Copies the release files into an install directory, stops any running
  instance, replaces the binary, (re)registers the logon task, and starts
  the app. Safe to re-run for upgrades; stats.json and logs are preserved.

.PARAMETER InstallDir
  Target directory. Default: %LOCALAPPDATA%\AcerInputFix

.PARAMETER NoStart
  Install files and startup task but do not launch AcerInputFix now.

.PARAMETER NoStartupTask
  Skip scheduled-task registration (manual start only).
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "AcerInputFix"),
    [switch]$NoStart,
    [switch]$NoStartupTask
)

$ErrorActionPreference = "Stop"

$TaskName = "AcerInputFix"
$SourceDir = $PSScriptRoot
$SourceExe = Join-Path $SourceDir "AcerInputFix.exe"

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator
    )
}

function Stop-AcerInputFixProcess {
    $procs = @(Get-Process -Name "AcerInputFix" -ErrorAction SilentlyContinue)
    if ($procs.Count -eq 0) {
        return
    }

    Write-Host "Stopping running AcerInputFix ($($procs.Count) process(es))..."

    foreach ($proc in $procs) {
        try {
            $proc.CloseMainWindow() | Out-Null
        }
        catch {
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (
        [DateTime]::UtcNow -lt $deadline -and
        (Get-Process -Name "AcerInputFix" -ErrorAction SilentlyContinue)
    ) {
        Start-Sleep -Milliseconds 200
    }

    Get-Process -Name "AcerInputFix" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    while (
        [DateTime]::UtcNow -lt $deadline -and
        (Get-Process -Name "AcerInputFix" -ErrorAction SilentlyContinue)
    ) {
        Start-Sleep -Milliseconds 200
    }

    if (Get-Process -Name "AcerInputFix" -ErrorAction SilentlyContinue) {
        throw "Could not stop AcerInputFix. Close it from the tray and retry."
    }
}

if (-not (Test-Path -LiteralPath $SourceExe)) {
    throw "AcerInputFix.exe not found next to Install.ps1 at $SourceDir"
}

if (-not (Test-IsAdministrator)) {
    Write-Host "Re-launching Install.ps1 as Administrator (required for Col07 auto-reset task)..."
    $argList = @(
        "-NoProfile"
        "-ExecutionPolicy", "Bypass"
        "-File", "`"$PSCommandPath`""
        "-InstallDir", "`"$InstallDir`""
    )
    if ($NoStart) { $argList += "-NoStart" }
    if ($NoStartupTask) { $argList += "-NoStartupTask" }

    $p = Start-Process `
        -FilePath "powershell.exe" `
        -Verb RunAs `
        -ArgumentList $argList `
        -Wait `
        -PassThru

    exit $p.ExitCode
}

$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
$TargetExe = Join-Path $InstallDir "AcerInputFix.exe"

Write-Host "AcerInputFix install / upgrade"
Write-Host "  Source : $SourceDir"
Write-Host "  Target : $InstallDir"

New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

Stop-AcerInputFixProcess

# Preserve user data across upgrades.
$preserve = @("stats.json", "AcerInputFix.log", "AcerInputFix.old.log")
$backup = Join-Path $env:TEMP ("AcerInputFix-preserve-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $backup | Out-Null
try {
    foreach ($name in $preserve) {
        $path = Join-Path $InstallDir $name
        if (Test-Path -LiteralPath $path) {
            Copy-Item -LiteralPath $path -Destination $backup -Force
        }
    }

    $filesToCopy = @(
        "AcerInputFix.exe"
        "Install.ps1"
        "Uninstall.ps1"
        "Install-StartupTask.ps1"
        "Remove-StartupTask.ps1"
        "README.md"
        "DIAGNOSTICS.md"
    )

    foreach ($name in $filesToCopy) {
        $from = Join-Path $SourceDir $name
        if (Test-Path -LiteralPath $from) {
            Copy-Item -LiteralPath $from -Destination $InstallDir -Force
        }
    }

    foreach ($name in $preserve) {
        $from = Join-Path $backup $name
        if (Test-Path -LiteralPath $from) {
            Copy-Item -LiteralPath $from -Destination $InstallDir -Force
        }
    }
}
finally {
    Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
}

$versionInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($TargetExe)
$version = $versionInfo.ProductVersion
if ([string]::IsNullOrWhiteSpace($version)) {
    $version = $versionInfo.FileVersion
}

Set-Content -LiteralPath (Join-Path $InstallDir "installed-version.txt") `
    -Value $version `
    -Encoding UTF8

if (-not $NoStartupTask) {
    & (Join-Path $InstallDir "Install-StartupTask.ps1") -ExePath $TargetExe
}

if (-not $NoStart) {
    Write-Host "Starting AcerInputFix..."
    if (-not $NoStartupTask) {
        try {
            Start-ScheduledTask -TaskName $TaskName -ErrorAction Stop
        }
        catch {
            Start-Process -FilePath $TargetExe
        }
    }
    else {
        Start-Process -FilePath $TargetExe
    }
}

Write-Host ""
Write-Host "Installed AcerInputFix $version to $InstallDir"
Write-Host "Upgrade later by running Install.ps1 from a newer release zip."
