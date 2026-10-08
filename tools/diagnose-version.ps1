# 版本号诊断脚本 - 追踪 build.counter 变化

param(
    [switch]$Monitor = $false
)

$rootPath = Resolve-Path (Join-Path $PSScriptRoot "..")
$counterFile = Join-Path $rootPath "build.counter"

function Show-CounterState {
    param([string]$Label)
    
    if (Test-Path $counterFile) {
        $value = Get-Content $counterFile -Raw | ForEach-Object { $_.Trim() }
        $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss.fff"
        Write-Host "[$timestamp] $Label : build.counter = $value" -ForegroundColor Cyan
    } else {
        Write-Host "[$Label]: build.counter NOT FOUND" -ForegroundColor Red
    }
}

if ($Monitor) {
    # 监控模式：持续检查文件变化
    Write-Host "Starting build.counter monitor (Ctrl+C to stop)..." -ForegroundColor Green
    Write-Host ""
    
    $lastHash = $null
    $interval = 100  # 毫秒
    
    while ($true) {
        if (Test-Path $counterFile) {
            $content = Get-Content $counterFile -Raw
            $currentHash = ($content | Get-FileHash -Algorithm MD5).Hash
            
            if ($currentHash -ne $lastHash) {
                Show-CounterState "CHANGED"
                $lastHash = $currentHash
            }
        }
        
        Start-Sleep -Milliseconds $interval
    }
} else {
    # 单次检查模式
    Write-Host "Build Counter Diagnostic report" -ForegroundColor Green
    Write-Host "================================" -ForegroundColor Green
    Write-Host ""
    
    # 检查文件状态
    if (-not (Test-Path $counterFile)) {
        Write-Host "ERROR: build.counter file not found!" -ForegroundColor Red
        Write-Host "Location: $counterFile" -ForegroundColor Red
        exit 1
    }
    
    # 显示当前值
    $value = Get-Content $counterFile -Raw | ForEach-Object { $_.Trim() }
    $fileInfo = Get-Item $counterFile
    
    Write-Host "Current State:" -ForegroundColor Cyan
    Write-Host "  Location: $counterFile"
    Write-Host "  Value: $value"
    Write-Host "  Last Modified: $($fileInfo.LastWriteTime)"
    Write-Host "  Size: $($fileInfo.Length) bytes"
    Write-Host ""
    
    # 检查 Version.props
    Write-Host "Version.props Status:" -ForegroundColor Cyan
    $versionPropsPath = Join-Path $rootPath "Version.props"
    if (Test-Path $versionPropsPath) {
        [xml]$versionXml = Get-Content $versionPropsPath
        $versionBase = $versionXml.Project.PropertyGroup.VersionBase
        if ($versionBase -is [System.Xml.XmlElement]) {
            $versionBase = $versionBase.InnerText
        }
        $buildCounterNode = $versionXml.Project.PropertyGroup.BuildCounter
        if ($buildCounterNode -is [System.Xml.XmlElement]) {
            $buildCounterDisplay = $buildCounterNode.InnerText
        } else {
            $buildCounterDisplay = $buildCounterNode
        }
        Write-Host "  VersionBase: $versionBase"
        Write-Host "  BuildCounter (in XML): $buildCounterDisplay"
        Write-Host "  Full Version Pattern: $versionBase" + '.$' + "(BuildCounter)"
    }
    Write-Host ""
    
    # 检查 Directory.Build.props
    Write-Host "Directory.Build.props Status:" -ForegroundColor Cyan
    $dirBuildPropsPath = Join-Path $rootPath "Directory.Build.props"
    if (Test-Path $dirBuildPropsPath) {
        $content = Get-Content $dirBuildPropsPath -Raw
        if ($content -match 'BuildCounter') {
            Write-Host "  ✓ Contains BuildCounter property"
        } else {
            Write-Host "  ✗ Does NOT contain BuildCounter property" -ForegroundColor Yellow
        }
    }
    Write-Host ""
    
    # 检查 .csproj 文件中的硬编码版本号
    Write-Host ".csproj Files with Hardcoded Versions:" -ForegroundColor Cyan
    $csprojFiles = Get-ChildItem -Path $rootPath -Recurse -Include "*.csproj" | Where-Object { $_.FullName -notlike "*/obj/*" }
    
    foreach ($csproj in $csprojFiles) {
        [xml]$proj = Get-Content $csproj.FullName
        $asmVersion = $proj.Project.PropertyGroup.AssemblyVersion | Select-Object -First 1
        $fileVersion = $proj.Project.PropertyGroup.FileVersion | Select-Object -First 1
        
        if ($asmVersion -or $fileVersion) {
            $relPath = $csproj.FullName.Replace($rootPath, "").TrimStart("\")
            Write-Host "  $relPath"
            if ($asmVersion) { Write-Host "    AssemblyVersion: $asmVersion" }
            if ($fileVersion) { Write-Host "    FileVersion: $fileVersion" }
        }
    }
    Write-Host ""
    
    Write-Host "Diagnostic Summary:" -ForegroundColor Green
    Write-Host "  • Current build.counter value: $value"
    Write-Host "  • This value needs to be used in build command:"
    Write-Host "    /p:BuildCounter=$value"
    Write-Host ""
    
    # Check if hardcoded versions still exist
    $hasHardcodedVersions = $false
    foreach ($csproj in $csprojFiles) {
        $content = Get-Content $csproj.FullName -Raw
        if ($content -match '<AssemblyVersion>[^$].*?</AssemblyVersion>' -or 
            $content -match '<FileVersion>[^$].*?</FileVersion>') {
            $hasHardcodedVersions = $true
            break
        }
    }
    
    Write-Host "⚠ Issues Detected:" -ForegroundColor Yellow
    if ($hasHardcodedVersions) {
        Write-Host "  • Multiple .csproj files have hardcoded AssemblyVersion" -ForegroundColor Yellow
        Write-Host "  • These values override the dynamic Version.props setting" -ForegroundColor Yellow
        Write-Host "  • Run fix-csproj-versions.ps1 to fix them" -ForegroundColor Yellow
    } else {
        Write-Host "  ✓ All hardcoded versions have been removed!" -ForegroundColor Green
        Write-Host "  ✓ Version system is correctly configured" -ForegroundColor Green
    }
    Write-Host ""
}
