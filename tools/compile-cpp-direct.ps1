# 直接用 MSVC 编译器编译 C++ 项目
# 绕过 MSBuild 工具集验证问题

param(
    [string]$Configuration = "Release",
    [string]$Platform = "x64"
)

# 编译器路径
$compiler = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.50.35717\bin\Hostx64\x64\cl.exe"
$linker = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.50.35717\bin\Hostx64\x64\lib.exe"

# Windows SDK 路径
$winsdkInclude = "C:\Program Files (x86)\Windows Kits\10\Include\10.0.26100.0"
$winsdkLib = "C:\Program Files (x86)\Windows Kits\10\lib\10.0.26100.0"

# MSVC 路径
$msvcPath = "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Tools\MSVC\14.50.35717"

Write-Host "=== ABot C++ 直接编译 ===" -ForegroundColor Cyan
Write-Host "配置: $Configuration | 平台: $Platform" -ForegroundColor Gray

# 源文件目录
$sourceDir = "ABot\ABot.Core\src"
$includeDir = "ABot\ABot.Core\include"
$outDir = "bin\$Configuration\$Platform"
$objDir = "obj\$Configuration\ABot.Core\$Platform"

# 创建输出目录
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
New-Item -ItemType Directory -Force -Path $objDir | Out-Null

# 编译标志
$cxxFlags = @(
    "/std:c++17",
    "/W4",
    "/nologo",
    "/c"
)

if ($Configuration -eq "Debug") {
    $cxxFlags += @("/Od", "/Zi", "/RTC1", "/MDd", "/D_DEBUG")
} else {
    $cxxFlags += @("/O2", "/Oy", "/MD", "/DNDEBUG")
}

# 包含目录
$includeFlags = @(
    "/I`"$includeDir`"",
    "/I`"$sourceDir`"",
    "/I`"$msvcPath\include`"",
    "/I`"$winsdkInclude\ucrt`"",
    "/I`"$winsdkInclude\um`"",
    "/I`"$winsdkInclude\shared`""
)

# 预处理定义
$defineFlags = @(
    "/D_CRT_SECURE_NO_WARNINGS",
    "/D_WINDOWS",
    "/D_WINDLL",
    "/DWIN32"
)

# 查找所有源文件
$sourceFiles = Get-ChildItem -Path $sourceDir -Filter "*.cpp" | Select-Object -ExpandProperty FullName

if ($sourceFiles.Count -eq 0) {
    Write-Host "❌ 未找到任何 .cpp 文件在 $sourceDir" -ForegroundColor Red
    exit 1
}

Write-Host "`n编译源文件..." -ForegroundColor Cyan
$objectFiles = @()

foreach ($file in $sourceFiles) {
    $fileName = [System.IO.Path]::GetFileNameWithoutExtension($file)
    $objFile = Join-Path $objDir "$fileName.obj"
    
    Write-Host "  编译: $(Split-Path $file -Leaf)" -ForegroundColor Gray
    
    # 编译命令
    $compileCmd = @(
        $cxxFlags,
        $includeFlags,
        $defineFlags,
        "/Fo`"$objFile`"",
        "`"$file`""
    ) -join " "
    
    # 执行编译
    & $compiler $cxxFlags $includeFlags $defineFlags "/Fo$objFile" "$file" 2>&1 | ForEach-Object {
        if ($_ -match "error") {
            Write-Host "    ❌ $_" -ForegroundColor Red
        } elseif ($_ -match "warning") {
            Write-Host "    ⚠️  $_" -ForegroundColor Yellow
        }
    }
    
    if ($LASTEXITCODE -eq 0) {
        $objectFiles += $objFile
        Write-Host "    ✓ 完成" -ForegroundColor Green
    } else {
        Write-Host "    ❌ 编译失败" -ForegroundColor Red
        exit 1
    }
}

Write-Host "`n链接库文件..." -ForegroundColor Cyan

# 输出库文件名
$libFile = Join-Path $outDir "ABot.Core.lib"

# 链接器库路径
$libFlags = @(
    "/LIBPATH:`"$msvcPath\lib\x64`"",
    "/LIBPATH:`"$msvcPath\atlmfc\lib\x64`"",
    "/LIBPATH:`"$winsdkLib\ucrt\x64`"",
    "/LIBPATH:`"$winsdkLib\um\x64`""
)

# 链接命令
$linkCmd = @($objectFiles) + $libFlags -join " "

& $linker $objectFiles $libFlags "/OUT:$libFile" 2>&1 | ForEach-Object {
    if ($_ -match "error") {
        Write-Host "  ❌ $_" -ForegroundColor Red
    } else {
        Write-Host "  $_" -ForegroundColor Gray
    }
}

if ($LASTEXITCODE -eq 0) {
    Write-Host "`n✅ 编译成功！" -ForegroundColor Green
    Write-Host "生成的库文件: $libFile" -ForegroundColor Green
    exit 0
} else {
    Write-Host "`n❌ 链接失败" -ForegroundColor Red
    exit 1
}
