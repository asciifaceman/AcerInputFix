<#
.SYNOPSIS
  Uninstall AcerInputFix.

.DESCRIPTION
  Stops the running process, removes the logon scheduled task, and deletes
  the install directory. Optionally keeps logs and stats.json.

.PARAMETER InstallDir
  Directory to remove. Default: %LOCALAPPDATA%\AcerInputFix

.PARAMETER KeepData
  Leave stats.json and log files behind.
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "AcerInputFix"),
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"

$TaskName = "AcerInputFix"

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

    Write-Host "Stopping running AcerInputFix..."

    foreach ($proc in $procs) {
        try {
            $proc.CloseMainWindow() | Out-Null
        }
        catch {
        }
    }

    Start-Sleep -Milliseconds 500

    Get-Process -Name "AcerInputFix" -ErrorAction SilentlyContinue |
        Stop-Process -Force -ErrorAction SilentlyContinue

    Start-Sleep -Milliseconds 300
}

if (-not (Test-IsAdministrator)) {
    Write-Host "Re-launching Uninstall.ps1 as Administrator..."
    $argList = @(
        "-NoProfile"
        "-ExecutionPolicy", "Bypass"
        "-File", "`"$PSCommandPath`""
        "-InstallDir", "`"$InstallDir`""
    )
    if ($KeepData) { $argList += "-KeepData" }

    $p = Start-Process `
        -FilePath "powershell.exe" `
        -Verb RunAs `
        -ArgumentList $argList `
        -Wait `
        -PassThru

    exit $p.ExitCode
}

$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)

Write-Host "AcerInputFix uninstall"
Write-Host "  Target: $InstallDir"

Stop-AcerInputFixProcess

Unregister-ScheduledTask `
    -TaskName $TaskName `
    -Confirm:$false `
    -ErrorAction SilentlyContinue

Write-Host "Startup task removed (if it existed)."

if (-not (Test-Path -LiteralPath $InstallDir)) {
    Write-Host "Install directory already absent."
    return
}

if ($KeepData) {
    $remove = @(
        "AcerInputFix.exe"
        "Install.ps1"
        "Uninstall.ps1"
        "Install-StartupTask.ps1"
        "Remove-StartupTask.ps1"
        "README.md"
        "DIAGNOSTICS.md"
        "installed-version.txt"
    )

    foreach ($name in $remove) {
        $path = Join-Path $InstallDir $name
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    Write-Host "Program files removed; data left in $InstallDir"
}
else {
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
    Write-Host "Removed $InstallDir"
}

Write-Host "AcerInputFix uninstalled."
