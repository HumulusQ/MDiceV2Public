# MDiceV2 Debug Build Script - Fixed Version
# Simple and reliable debug build

param(
    [switch]$NoClean,
    [string]$Output = "MDiceV2_Debug"
)

$ErrorActionPreference = "Stop"
$rootPath = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputPath = Join-Path $rootPath $Output

Write-Host ""
Write-Host "========== MDiceV2 Debug Build ==========" -ForegroundColor Cyan
Write-Host "Root: $rootPath"
Write-Host "Output: $outputPath"
Write-Host ""

try {
    # Check dotnet
    Write-Host "Checking environment..." -ForegroundColor Cyan
    $version = dotnet --version
    Write-Host "[+] dotnet $version" -ForegroundColor Green
    Write-Host ""
    
    # Clean build artifacts
    if (-not $NoClean) {
        Write-Host "Cleaning build artifacts..." -ForegroundColor Cyan
        
        $cleanPaths = @(
            "bin", "obj",
            "MDiceV2.Abstractions\bin", "MDiceV2.Abstractions\obj",
            "MDiceV2.Interfaces\bin", "MDiceV2.Interfaces\obj",
            "MDiceV2.Core\bin", "MDiceV2.Core\obj",
            "MDiceV2.Launcher\bin", "MDiceV2.Launcher\obj",
            "MDiceV2.Console\bin", "MDiceV2.Console\obj",
            "MDiceV2.Tests\bin", "MDiceV2.Tests\obj",
            "Mods\CustomizedReply\bin", "Mods\CustomizedReply\obj",
            "Mods\AIMod\bin", "Mods\AIMod\obj",
            "Mods\ABot\bin", "Mods\ABot\obj"
        )
        
        foreach ($path in $cleanPaths) {
            $fullPath = Join-Path $rootPath $path
            if (Test-Path $fullPath) {
                Remove-Item -Path $fullPath -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
            }
        }
        Write-Host "[+] Cleaned" -ForegroundColor Green
        Write-Host ""
    }
    
    # Build
    Write-Host "Building MDiceV2.Launcher..." -NoNewline
    $output = dotnet build (Join-Path $rootPath "MDiceV2.Launcher\MDiceV2.Launcher.csproj") -c Debug 2>&1 | Select-String "error|failed" | Select-Object -First 1
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host " [OK]" -ForegroundColor Green
    } else {
        Write-Host " [FAILED]" -ForegroundColor Red
        if ($output) { Write-Host "  Error: $output" -ForegroundColor Red }
        throw "Build failed"
    }
    Write-Host ""
    
    # Publish
    Write-Host "Publishing debug version..." -NoNewline
    dotnet publish (Join-Path $rootPath "MDiceV2.Launcher\MDiceV2.Launcher.csproj") -c Debug -o $outputPath --no-build 2>&1 | Out-Null
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host " [OK]" -ForegroundColor Green
        
        $exePath = Join-Path $outputPath "MDiceV2.Launcher.exe"
        if (Test-Path $exePath) {
            $size = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
            Write-Host "  Generated: MDiceV2.Launcher.exe ($size MB)" -ForegroundColor Green
        }
    } else {
        Write-Host " [FAILED]" -ForegroundColor Red
        throw "Publish failed"
    }
    
    Write-Host ""
    Write-Host "[+] Debug build completed!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Run the debug version:" -ForegroundColor Cyan
    Write-Host "  .\$Output\MDiceV2.Launcher.exe" -ForegroundColor Yellow
    Write-Host ""
    
} catch {
    Write-Host ""
    Write-Host "[ERROR] $_" -ForegroundColor Red
    exit 1
}
