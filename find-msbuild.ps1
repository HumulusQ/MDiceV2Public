# Quick MSBuild Path Finder
# 快速找到 MSBuild 的工具脚本

Write-Host "========== Searching for MSBuild ==========`n"

$commonPaths = @(
    "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\msbuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\EnterpriseInstallation\Build Tools\MSBuild\Current\Bin\msbuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\msbuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\msbuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\15.0\Bin\msbuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio 14.0\MSBuild\14.0\Bin\msbuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio 15.0\MSBuild\15.0\Bin\msbuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio 16.0\MSBuild\16.0\Bin\msbuild.exe"
)

$found = $false
foreach ($path in $commonPaths) {
    if (Test-Path $path) {
        Write-Host "✓ FOUND: $path" -ForegroundColor Green
        
        # 验证它能运行
        try {
            $version = & $path /version 2>&1 | Select-Object -First 1
            Write-Host "  Version: $version" -ForegroundColor Green
            $found = $true
        } catch {
            Write-Host "  (Found but cannot execute)" -ForegroundColor Yellow
        }
    }
}

if (-not $found) {
    Write-Host "`nSearching in Program Files directories..." -ForegroundColor Yellow
    
    # 手动搜索
    $vsRoots = @(
        "C:\Program Files\Microsoft Visual Studio",
        "C:\Program Files (x86)\Microsoft Visual Studio"
    )
    
    foreach ($root in $vsRoots) {
        if (Test-Path $root) {
            Write-Host "`nSearching in: $root"
            $msbuildFiles = Get-ChildItem -Path $root -Filter "msbuild.exe" -Recurse -ErrorAction SilentlyContinue
            foreach ($file in $msbuildFiles) {
                Write-Host "  Found: $($file.FullName)" -ForegroundColor Green
            }
        }
    }
}

Write-Host "`n========== End of Search ==========`n"
