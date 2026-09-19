$ErrorActionPreference = "Stop"

$TaskName = "AcerInputFix"
$Exe = Join-Path $PSScriptRoot "AcerInputFix.exe"

if (-not (Test-Path $Exe)) {
    throw "AcerInputFix.exe not found at $Exe"
}

$User = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name

$Action = New-ScheduledTaskAction `
    -Execute $Exe

$Trigger = New-ScheduledTaskTrigger `
    -AtLogOn `
    -User $User

# Highest is required for automatic Col07 disable/enable (Layer-2 taskbar
# repair). Limited still gets Layer-1 B0 suppression only.
$Principal = New-ScheduledTaskPrincipal `
    -UserId $User `
    -LogonType Interactive `
    -RunLevel Highest

$Settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $Action `
    -Trigger $Trigger `
    -Principal $Principal `
    -Settings $Settings `
    -Description "Work around Acer stuck VK_MEDIA_NEXT_TRACK / Col07 input state." `
    -Force

Write-Host ""
Write-Host "AcerInputFix startup task installed (RunLevel Highest)."
Write-Host "It will start elevated when $User signs in (UAC consent may apply once at install)."
Write-Host "Layer-1: suppress stuck VK_MEDIA_NEXT_TRACK. Layer-2: auto Col07 reset after each repair."
