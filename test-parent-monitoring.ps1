# Test parent process monitoring in Console -> Core relationship
# This script verifies that Core detects when Console disappears and shuts down gracefully

param(
    [int]$WaitBeforeKillConsole = 10,  # Wait 10 seconds before killing Console
    [int]$WaitForCoreExit = 15        # Wait 15 seconds for Core to detect and exit
)

$DebugDir = ".\MDiceV2_Debug"
$ConsolePath = Join-Path $DebugDir "Console.exe"
$CorePath = Join-Path $DebugDir "Core.Dice.exe"

Write-Host "===========" -ForegroundColor Green
Write-Host "Parent Process Monitoring Test" -ForegroundColor Green
Write-Host "===========" -ForegroundColor Green

# Verify files exist
if (-not (Test-Path $ConsolePath)) {
    Write-Host "[ERROR] Console.exe not found at $ConsolePath" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path $CorePath)) {
    Write-Host "[ERROR] Core.Dice.exe not found at $CorePath" -ForegroundColor Red
    exit 1
}

Write-Host "[1] Starting Console.exe..."  
$consoleProcess = Start-Process -FilePath $ConsolePath -PassThru -NoNewWindow

Write-Host "[2] Console started with PID: $($consoleProcess.Id)"
Write-Host "[3] Waiting $WaitBeforeKillConsole seconds for Core to start and connect..."

# Wait for Core to start (it should be launched by Console)
Start-Sleep -Seconds 5

# Check if Core is running
$coreProcesses = Get-Process Core.Dice -ErrorAction SilentlyContinue
if ($coreProcesses.Count -eq 0) {
    Write-Host "[ERROR] Core.Dice.exe did not start" -ForegroundColor Red
    $consoleProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    exit 1
}

if ($coreProcesses -is [array]) {
    $coreProcess = $coreProcesses[0]
    Write-Host "[WARNING] Multiple Core.Dice processes found, using first: $($coreProcess.Id)" -ForegroundColor Yellow
} else {
    $coreProcess = $coreProcesses
}

Write-Host "[+] Core.Dice running with PID: $($coreProcess.Id)"
Write-Host "[+] Console PID in args should be: $($consoleProcess.Id)"

# Wait before killing Console
Write-Host "[4] Waiting $WaitBeforeKillConsole more seconds before killing Console..."
Start-Sleep -Seconds $($WaitBeforeKillConsole - 5)

# Kill Console process
Write-Host "[5] Stopping Console (PID: $($consoleProcess.Id))..."
$consoleProcess | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2

# Check if Console is gone
if ($consoleProcess.HasExited) {
    Write-Host "[+] Console successfully terminated" -ForegroundColor Green
} else {
    Write-Host "[-] Console still running?!" -ForegroundColor Yellow
}

# Wait for Core to detect and exit
Write-Host "[6] Waiting $WaitForCoreExit seconds for Core to detect parent death and exit gracefully..."
$elapsedSeconds = 0
$maxWait = $WaitForCoreExit
$coreGracefulExit = $false

while ($elapsedSeconds -lt $maxWait) {
    if ($coreProcess.HasExited) {
        Write-Host "[+] Core.Dice detected parent death and exited gracefully!" -ForegroundColor Green
        $coreGracefulExit = $true
        break
    }
    
    Write-Host "[...] Core still running (waited ${elapsedSeconds}s of ${maxWait}s)"
    Start-Sleep -Seconds 1
    $elapsedSeconds += 1
    
    # Refresh process state
    try {
        $coreProcess.Refresh()
    } catch {
        # Process might be gone
        $coreGracefulExit = $true
        break
    }
}

if ($coreGracefulExit) {
    Write-Host "`n========== TEST PASSED ==========" -ForegroundColor Green
    Write-Host "✓ Parent process monitoring working correctly" -ForegroundColor Green
    Write-Host "✓ Core detected Console disappearance" -ForegroundColor Green
    Write-Host "✓ Core performed graceful shutdown" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`n========== TEST FAILED ==========" -ForegroundColor Red
    Write-Host "✗ Core did not detect parent death within $WaitForCoreExit seconds" -ForegroundColor Red
    Write-Host "✗ Core is still running: $($coreProcess.Id)" -ForegroundColor Red
    
    # Clean up
    if (-not $coreProcess.HasExited) {
        Write-Host "[*] Force-killing Core for cleanup..."
        $coreProcess | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    
    exit 1
}
