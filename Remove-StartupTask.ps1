$TaskName = "AcerInputFix"

Unregister-ScheduledTask `
    -TaskName $TaskName `
    -Confirm:$false `
    -ErrorAction SilentlyContinue

Write-Host "AcerInputFix startup task removed."