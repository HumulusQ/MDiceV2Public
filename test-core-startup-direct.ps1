# 直接启动Console并观察其输出和Core的启动情况
$DebugDir = "c:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\MDiceV2_Debug"
$ConsolePath = Join-Path $DebugDir "MDiceV2.Console.exe"

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "Direct Core Startup Test" -ForegroundColor Cyan  
Write-Host "======================================" -ForegroundColor Cyan

if (-not (Test-Path $ConsolePath)) {
    Write-Host "[ERROR] Console.exe not found at $ConsolePath" -ForegroundColor Red
    exit 1
}

Write-Host "[1] Listing all dotnet/MDiceV2 processes before startup:"
Get-Process | Where-Object { $_.ProcessName -like "*dotnet*" -or $_.ProcessName -like "*MDiceV2*" -or $_.ProcessName -like "*Dice*" -or $_.ProcessName -like "*Console*" } | Format-Table ProcessName, Id, @{Label="Memory(MB)"; Expression={[math]::Round($_.WorkingSet/1MB, 2)}}

Write-Host "`n[2] Starting Console and capturing output for 2 seconds..."
$console = Start-Process -FilePath $ConsolePath -PassThru -NoNewWindow
$consoleId = $console.Id

Write-Host "[+] Console started with PID: $consoleId"
Start-Sleep -Seconds 2

Write-Host "`n[3] Checking all dotnet/MDiceV2 processes after startup:"
Get-Process | Where-Object { $_.ProcessName -like "*dotnet*" -or $_.ProcessName -like "*MDiceV2*" -or $_.ProcessName -like "*Dice*" -or $_.ProcessName -like "*Console*" } | Format-Table ProcessName, Id, @{Label="Memory(MB)"; Expression={[math]::Round($_.WorkingSet/1MB, 2)}}

# Get ALL processes to better understand
$allProcs = Get-Process | Where-Object { $_.Id -gt 1000 }
Write-Host "`n[4] All processes started after Console (ID > 1000):"
$allProcs | Where-Object { $_.StartTime -gt (Get-Date).AddSeconds(-5) } | Format-Table ProcessName, Id, StartTime -AutoSize

Write-Host "`n[5] Now killing Console process $consoleId..."
Stop-Process -Id $consoleId -Force -ErrorAction SilentlyContinue
Write-Host "[+] Console killed"

Write-Host "`n[6] Waiting 8 seconds and checking for remaining processes..."
Start-Sleep -Seconds 8

Write-Host "`n[7] Remaining processes (checking if Core.Dice still exists):"
$remaining = Get-Process | Where-Object { $_.ProcessName -like "*Core*" -or $_.ProcessName -like "*Dice*" }
if ($remaining) {
    Write-Host "[-] WARNING: Core process still running!" -ForegroundColor Red
    $remaining | Format-Table ProcessName, Id
    Write-Host "`n[8] Force-killing remaining Core processes..."
    $remaining | Stop-Process -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "[+] No remaining Core/Dice processes - cleanup successful!" -ForegroundColor Green
}
