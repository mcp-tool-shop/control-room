#!/usr/bin/env pwsh
# Test script that produces artifacts for Control Room

Write-Host "Starting Control Room artifact test..."
Write-Host "Current time: $(Get-Date)"
Write-Host ""

# Check if run directory is set (CONTROLROOM_RUN_DIR is the preferred env var)
$runDir = $env:CONTROLROOM_RUN_DIR
if (-not $runDir) {
    $runDir = $env:CONTROLROOM_ARTIFACT_DIR  # Legacy fallback
}
$runId = $env:CONTROLROOM_RUN_ID

if ($runDir) {
    Write-Host "Run directory: $runDir"
    Write-Host "Run ID: $runId"
    Write-Host ""
} else {
    Write-Host "No run directory set (running outside Control Room)"
    $runDir = "."
}

# Simulate some work
for ($i = 1; $i -le 5; $i++) {
    Write-Host "Processing item $i of 5..."
    Start-Sleep -Milliseconds 300
}

Write-Host ""
Write-Host "Creating artifacts..."

# Create a JSON artifact
$report = @{
    timestamp = (Get-Date -Format "o")
    runId = $runId
    status = "success"
    itemsProcessed = 5
    metrics = @{
        cpuTime = "0.5s"
        memoryPeak = "45MB"
    }
}
$reportPath = Join-Path $runDir "report.json"
$report | ConvertTo-Json -Depth 3 | Out-File -FilePath $reportPath -Encoding UTF8
Write-Host "  Created: report.json"

# Create a log artifact
$logPath = Join-Path $runDir "detailed.log"
@"
=== Detailed Execution Log ===
Started: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
Run ID: $runId

[INFO] Initializing...
[INFO] Loading configuration
[INFO] Processing items
[DEBUG] Item 1: OK
[DEBUG] Item 2: OK
[DEBUG] Item 3: OK
[DEBUG] Item 4: OK
[DEBUG] Item 5: OK
[INFO] All items processed successfully

Completed: $(Get-Date -Format "yyyy-MM-dd HH:mm:ss")
"@ | Out-File -FilePath $logPath -Encoding UTF8
Write-Host "  Created: detailed.log"

# Create a summary text file
$summaryPath = Join-Path $runDir "summary.txt"
@"
Run Summary
===========
Items Processed: 5
Status: Success
Duration: ~1.5 seconds
"@ | Out-File -FilePath $summaryPath -Encoding UTF8
Write-Host "  Created: summary.txt"

Write-Host ""
Write-Host "All artifacts created successfully!"
Write-Host "Script completed at $(Get-Date)"
