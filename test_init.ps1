# 测试 MessageProcessor 初始化
$ErrorActionPreference = "Stop"

Write-Host "开始测试..." -ForegroundColor Green

try {
    # 删除旧的data目录以确保是全新环境
    $dataPath = "C:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\MDiceV2.Launcher\bin\Debug\net10.0-windows\data"
    if (Test-Path $dataPath) {
        Write-Host "删除旧的data目录..." -ForegroundColor Yellow
        Remove-Item $dataPath -Recurse -Force
    }

    Write-Host "启动程序..." -ForegroundColor Green
    $process = Start-Process -FilePath "C:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\MDiceV2.Launcher\bin\Debug\net10.0-windows\MDiceV2.Launcher.exe" -PassThru -Wait

    Write-Host "程序退出，退出代码: $($process.ExitCode)" -ForegroundColor $(if ($process.ExitCode -eq 0) { "Green" } else { "Red" })
    
    # 检查是否创建了data目录
    if (Test-Path $dataPath) {
        Write-Host "data目录已创建" -ForegroundColor Green
        Get-ChildItem $dataPath
    } else {
        Write-Host "data目录未创建" -ForegroundColor Red
    }
}
catch {
    Write-Host "测试失败: $_" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Red
}
