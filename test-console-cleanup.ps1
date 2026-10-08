# 测试 Console 子进程自动清理功能

Write-Host "===== Console Child Process Cleanup Test =====" -ForegroundColor Cyan

# 1. 启动 Console，监视子进程
Write-Host "`n[Test 1] Starting Console and monitoring processes..." -ForegroundColor Yellow
$consoleProcess = Start-Process -FilePath "C:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\MDiceV2_Debug\MDiceV2.Console.exe" -PassThru
Write-Host "✓ Console started (PID: $($consoleProcess.Id))"

# 等待 Core 进程启动
Start-Sleep -Seconds 3

# 查找 Core.Dice 进程
$coreProcess = Get-Process -Name "MDiceV2.Core.Dice" -ErrorAction SilentlyContinue
if ($coreProcess) {
    Write-Host "✓ Core.Dice process found (PID: $($coreProcess.Id))" -ForegroundColor Green
} else {
    Write-Host "✗ Core.Dice process not found" -ForegroundColor Red
}

# 2. 测试 Ctrl+C
Write-Host "`n[Test 2] Sending Ctrl+C to Console..." -ForegroundColor Yellow
$consoleProcess.StandardInput.WriteLine(([char]3))  # Ctrl+C
Start-Sleep -Seconds 2

# 检查进程是否已清理
$coreProcessAfterCtrlC = Get-Process -Name "MDiceV2.Core.Dice" -ErrorAction SilentlyContinue
if ($null -eq $coreProcessAfterCtrlC) {
    Write-Host "✓ Core.Dice process cleaned up after Ctrl+C" -ForegroundColor Green
} else {
    Write-Host "✗ Core.Dice process still running after Ctrl+C" -ForegroundColor Red
}

# 3. 再次测试：启动、强制关闭
Write-Host "`n[Test 3] Testing force kill scenario..." -ForegroundColor Yellow
$consoleProcess2 = Start-Process -FilePath "C:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\MDiceV2_Debug\MDiceV2.Console.exe" -PassThru
Start-Sleep -Seconds 3

Write-Host "Stopping Console process..." 
Stop-Process -InputObject $consoleProcess2 -Force
Start-Sleep -Seconds 2

$coreProcessAfterStop = Get-Process -Name "MDiceV2.Core.Dice" -ErrorAction SilentlyContinue
if ($null -eq $coreProcessAfterStop) {
    Write-Host "✓ Core.Dice process cleaned up after force stop" -ForegroundColor Green
} else {
    Write-Host "✗ Core.Dice process still running after force stop" -ForegroundColor Red
}

Write-Host "`n===== Test Complete =====" -ForegroundColor Cyan
