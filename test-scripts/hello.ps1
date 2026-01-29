#!/usr/bin/env pwsh
# Simple test script for Control Room

Write-Host "Starting Control Room test script..."
Write-Host "Current time: $(Get-Date)"
Write-Host ""

for ($i = 1; $i -le 5; $i++) {
    Write-Host "Processing item $i of 5..."
    Start-Sleep -Milliseconds 500
}

Write-Host ""
Write-Host "All items processed successfully!"
Write-Host "Script completed at $(Get-Date)"
