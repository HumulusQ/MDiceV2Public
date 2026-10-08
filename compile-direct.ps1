# Handle System PoC - 直接编译脚本（指定 MSBuild 路径版本）
# 
# 使用已知的 MSBuild 路径直接编译
# 这个版本不需要 PATH 配置

# 用户提供的 MSBuild 路径
$msbuildPath = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\msbuild.exe"

# 验证路径
if (-not (Test-Path $msbuildPath)) {
    Write-Host "[ERROR] MSBuild not found at: $msbuildPath" -ForegroundColor Red
    Write-Host "`nTrying alternative paths..." -ForegroundColor Yellow
    
    # 尝试找到正确的文件
    $alternatives = @(
        "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\15.0\Bin\msbuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\msbuild.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\msbuild.exe"
    )
    
    $found = $false
    foreach ($alt in $alternatives) {
        if (Test-Path $alt) {
            Write-Host "✓ Found alternative: $alt" -ForegroundColor Green
            $msbuildPath = $alt
            $found = $true
            break
        }
    }
    
    if (-not $found) {
        Write-Host "ERROR: Could not find MSBuild anywhere!" -ForegroundColor Red
        Write-Host "Please run: PowerShell -File find-msbuild.ps1" -ForegroundColor Yellow
        exit 1
    }
}

Write-Host "Using MSBuild: $msbuildPath`n" -ForegroundColor Green

# 设置路径
$ProjectRoot = "C:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2"
$CoreProjectPath = "$ProjectRoot\ABot\ABot.Core"
$ProjectFile = "$CoreProjectPath\ABot.Core.vcxproj"
$OutputDir = "$CoreProjectPath\bin\Debug\x64"
$Configuration = "Debug"
$Platform = "x64"

Write-Host "========== HANDLE POC DIRECT COMPILATION ==========`n"

# 验证项目文件
if (-not (Test-Path $ProjectFile)) {
    Write-Host "ERROR: Project file not found: $ProjectFile" -ForegroundColor Red
    exit 1
}

Write-Host "✓ Project file found`n"

# ============================================================================
# Legacy 模式编译
# ============================================================================
Write-Host "========== Phase 1: Legacy Mode Compilation ==========" -ForegroundColor Cyan
Write-Host "Building with USE_HANDLES=OFF...`n"

$startTime = Get-Date
try {
    & $msbuildPath $ProjectFile `
        /p:Configuration=$Configuration `
        /p:Platform=$Platform `
        /p:DebugSymbols=true `
        /p:USE_HANDLES=OFF `
        /m:4 `
        /verbosity:minimal
    
    $exitCode = $LASTEXITCODE
} catch {
    Write-Host "ERROR: Compilation failed: $_" -ForegroundColor Red
    exit 1
}
$endTime = Get-Date
$duration = [math]::Round(($endTime - $startTime).TotalSeconds, 2)

if ($exitCode -eq 0) {
    Write-Host "`n✓ Legacy mode compilation successful (${duration}s)`n" -ForegroundColor Green
} else {
    Write-Host "`n✗ Legacy mode compilation FAILED (exit code: $exitCode)`n" -ForegroundColor Red
    exit 1
}

# ============================================================================
# Handle 模式编译
# ============================================================================
Write-Host "========== Phase 2: Handle Mode Compilation ==========" -ForegroundColor Cyan
Write-Host "Building with USE_HANDLES=ON...`n"

$startTime = Get-Date
try {
    & $msbuildPath $ProjectFile `
        /p:Configuration=$Configuration `
        /p:Platform=$Platform `
        /p:DebugSymbols=true `
        /p:USE_HANDLES=ON `
        /m:4 `
        /verbosity:minimal
    
    $exitCode = $LASTEXITCODE
} catch {
    Write-Host "ERROR: Compilation failed: $_" -ForegroundColor Red
    exit 1
}
$endTime = Get-Date
$duration = [math]::Round(($endTime - $startTime).TotalSeconds, 2)

if ($exitCode -eq 0) {
    Write-Host "`n✓ Handle mode compilation successful (${duration}s)`n" -ForegroundColor Green
} else {
    Write-Host "`n✗ Handle mode compilation FAILED (exit code: $exitCode)`n" -ForegroundColor Red
    exit 1
}

# ============================================================================
# 验证生成的文件
# ============================================================================
Write-Host "========== Phase 3: Verification ==========" -ForegroundColor Cyan

$dllPath = "$OutputDir\ABot.Core.dll"
if (Test-Path $dllPath) {
    $fileInfo = Get-Item $dllPath
    $sizeMB = [math]::Round($fileInfo.Length / 1MB, 2)
    Write-Host "`n✓ DLL generated: ABot.Core.dll ($($sizeMB) MB)" -ForegroundColor Green
    Write-Host "  Location: $dllPath`n"
} else {
    Write-Host "`n✗ DLL not found: $dllPath" -ForegroundColor Red
    exit 1
}

# ============================================================================
# 完成报告
# ============================================================================
Write-Host "========== COMPILATION SUCCESSFUL ==========" -ForegroundColor Green
Write-Host "Both Legacy and Handle modes compiled successfully!`n"

Write-Host "Summary:" -ForegroundColor Cyan
Write-Host "  Project:  ABot.Core"
Write-Host "  Config:   Debug x64"
Write-Host "  Output:   $OutputDir"
Write-Host "  Status:   ✓ Ready for testing`n"

exit 0
