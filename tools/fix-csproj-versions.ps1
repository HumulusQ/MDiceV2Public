# 修复 .csproj 文件中硬编码的版本号
# 这些硬编码版本号会覆盖 Version.props 中的动态版本号

param(
    [switch]$DryRun = $false,
    [switch]$Confirm = $false
)

$rootPath = Resolve-Path (Join-Path $PSScriptRoot "..")

Write-Host "Fixing hardcoded versions in .csproj files..." -ForegroundColor Cyan
Write-Host ""

# 找到所有 .csproj 文件（排除 obj 目录）
$csprojFiles = Get-ChildItem -Path $rootPath -Recurse -Include "*.csproj" | Where-Object {
    $_.FullName -notlike "*/obj/*" -and $_.FullName -notlike "*\.obj\*"
}

$filesToFix = @()

foreach ($csproj in $csprojFiles) {
    [xml]$proj = Get-Content $csproj.FullName
    
    $hasHardcodedVersion = $false
    $asmVersion = $null
    $fileVersion = $null
    
    # 检查 AssemblyVersion
    $asmVersionNode = $proj.Project.PropertyGroup.AssemblyVersion | Select-Object -First 1
    if ($asmVersionNode) {
        $asmVersion = $asmVersionNode.InnerText
        if ($asmVersion -and $asmVersion -notmatch '\$\(') {
            $hasHardcodedVersion = $true
        }
    }
    
    # 检查 FileVersion  
    $fileVersionNode = $proj.Project.PropertyGroup.FileVersion | Select-Object -First 1
    if ($fileVersionNode) {
        $fileVersion = $fileVersionNode.InnerText
        if ($fileVersion -and $fileVersion -notmatch '\$\(') {
            $hasHardcodedVersion = $true
        }
    }
    
    if ($hasHardcodedVersion) {
        $relPath = $csproj.FullName.Replace($rootPath, "").TrimStart("\")
        $filesToFix += @{
            Path = $csproj.FullName
            RelPath = $relPath
            AssemblyVersion = $asmVersion
            FileVersion = $fileVersion
        }
        
        Write-Host "Found: $relPath" -ForegroundColor Yellow
        Write-Host "  AssemblyVersion: $asmVersion" -ForegroundColor Gray
        Write-Host "  FileVersion: $fileVersion" -ForegroundColor Gray
    }
}

Write-Host ""

if ($filesToFix.Count -eq 0) {
    Write-Host "No hardcoded versions found. All good!" -ForegroundColor Green
    exit 0
}

Write-Host "Total files to fix: $($filesToFix.Count)" -ForegroundColor Cyan
Write-Host ""

if ($DryRun) {
    Write-Host "[DRY RUN] Would fix the above files" -ForegroundColor Yellow
    Write-Host "Run without -DryRun to apply changes" -ForegroundColor Yellow
    exit 0
}

if (-not $Confirm) {
    Write-Host "This will remove hardcoded AssemblyVersion and FileVersion from .csproj files" -ForegroundColor Yellow
    Write-Host "These will now use the dynamic version from Version.props instead" -ForegroundColor Yellow
    Write-Host ""
    
    $response = Read-Host "Continue? (yes/no)"
    if ($response -ne "yes") {
        Write-Host "Cancelled" -ForegroundColor Yellow
        exit 0
    }
}

Write-Host "Fixing files..." -ForegroundColor Cyan
Write-Host ""

foreach ($file in $filesToFix) {
    [xml]$proj = Get-Content $file.Path
    $modified = $false
    
    # 移除 AssemblyVersion 节点
    $asmVersionNodes = $proj.Project.PropertyGroup.AssemblyVersion
    foreach ($node in $asmVersionNodes) {
        if ($node.InnerText -and $node.InnerText -notmatch '\$\(') {
            $proj.Project.PropertyGroup.RemoveChild($node) | Out-Null
            $modified = $true
            Write-Host "Removed AssemblyVersion: $($file.RelPath)" -ForegroundColor Green
        }
    }
    
    # 移除 FileVersion 节点
    $fileVersionNodes = $proj.Project.PropertyGroup.FileVersion
    foreach ($node in $fileVersionNodes) {
        if ($node.InnerText -and $node.InnerText -notmatch '\$\(') {
            $proj.Project.PropertyGroup.RemoveChild($node) | Out-Null
            $modified = $true
            Write-Host "Removed FileVersion: $($file.RelPath)" -ForegroundColor Green
        }
    }
    
    # 保存修改
    if ($modified) {
        # 创建备份
        $backup = "$($file.Path).backup"
        Copy-Item -Path $file.Path -Destination $backup -Force
        
        # 保存修改后的 XML
        $settings = New-Object System.Xml.XmlWriterSettings
        $settings.Indent = $true
        $settings.IndentChars = "  "
        $settings.NewLineChars = "`r`n"
        
        $writer = [System.Xml.XmlWriter]::Create($file.Path, $settings)
        $proj.WriteTo($writer)
        $writer.Flush()
        $writer.Close()
        
        Write-Host "  ✓ Saved $($file.RelPath)" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "✓ All files fixed!" -ForegroundColor Green
Write-Host ""
Write-Host "Now these projects will use the dynamic version from Version.props:" -ForegroundColor Cyan
Write-Host "  Version: 0.2.6.\$(BuildCounter)" -ForegroundColor Cyan
Write-Host ""
Write-Host "The build.counter file will be automatically incremented on each build" -ForegroundColor Cyan
