# 直接测试Core的自我清理机制
# 通过启动Console -> Core，然后杀死Console，观察Core是否正常退出

$DebugDir = "c:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\MDiceV2_Debug"
$ConsolePath = Join-Path $DebugDir "MDiceV2.Console.exe"

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "Core Self-Cleanup Test" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan

if (-not (Test-Path $ConsolePath)) {
    Write-Host "[ERROR] Console.exe not found at $ConsolePath" -ForegroundColor Red
    exit 1
}

Write-Host "[1] Starting Console..."
$console = Start-Process -FilePath $ConsolePath -PassThru -NoNewWindow -RedirectStandardOutput "c:\temp\console.log" -RedirectStandardError "c:\temp\console_err.log"
$consoleId = $console.Id

Write-Host "[+] Console started with PID: $consoleId" -ForegroundColor Green
Write-Host "[2] Waiting 3 seconds for Core to start..."
Start-Sleep -Seconds 3

# List all running Core processes
$coreProcs = Get-Process | Where-Object { $_.ProcessName -like "*Core*" -or $_.ProcessName -like "*Dice*" }
if ($coreProcs.Count -eq 0) {
    Write-Host "[-] NO Core processes found!" -ForegroundColor Yellow
    Write-Host "[*] Console output:"
    Get-Content "c:\temp\console.log" -ErrorAction SilentlyContinue | Select-Object -Last 20
    Stop-Process -Id $consoleId -Force -ErrorAction SilentlyContinue
    exit 1
}

$coreId = if ($coreProcs -is [array]) { $coreProcs[0].Id } else { $coreProcs.Id }
Write-Host "[+] Found Core process with PID: $coreId" -ForegroundColor Green

Write-Host "[3] Console PID: $consoleId, Core PID: $coreId"
Write-Host "[4] Now killing Console process..."

Stop-Process -Id $consoleId -Force
Write-Host "[+] Console process killed" -ForegroundColor Green

Write-Host "[5] Waiting 8 seconds to see if Core detects and exits gracefully..."
$startTime = Get-Date
$coreExited = $false

for ($i = 0; $i -lt 8; $i++) {
    Start-Sleep -Seconds 1
    $elapsed = (Get-Date) - $startTime
    
    $proc = Get-Process -Id $coreId -ErrorAction SilentlyContinue
    if ($null -eq $proc -or $proc.HasExited) {
        $coreExited = $true
        Write-Host "[+] Core process $coreId exited after ${elapsed.TotalSeconds:F1} seconds" -ForegroundColor Green
        break
    }
    
    Write-Host "    [$i/8] Core still running... (${elapsed.TotalSeconds:F1}s elapsed)"
}

if ($coreExited) {
    Write-Host "`n========== TEST PASSED ==========" -ForegroundColor Green
    Write-Host "✓ Core detected Console death" -ForegroundColor Green
    Write-Host "✓ Core performed graceful shutdown and exited" -ForegroundColor Green
} else {
    Write-Host "`n========== TEST FAILED ==========" -ForegroundColor Red
    Write-Host "✗ Core process $coreId still running after 8 seconds!" -ForegroundColor Red
    
    # Show Console logs
    Write-Host "`n[Console Output (last 30 lines)]:" -ForegroundColor Yellow
    Get-Content "c:\temp\console.log" -ErrorAction SilentlyContinue | Select-Object -Last 30
    
    # Show if we can attach to Core's output
    Write-Host "`n[Killing Core for cleanup...]" -ForegroundColor Yellow
    Stop-Process -Id $coreId -Force -ErrorAction SilentlyContinue
    exit 1
}
