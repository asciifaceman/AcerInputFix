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

$Principal = New-ScheduledTaskPrincipal `
    -UserId $User `
    -LogonType Interactive `
    -RunLevel Limited

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
    -Description "Work around Acer stuck VK_MEDIA_NEXT_TRACK input state." `
    -Force

Write-Host ""
Write-Host "AcerInputFix startup task installed."
Write-Host "It will start automatically when $User signs in."