# MDiceV2 Debug Build Script - Fixed Version
# Simplified and corrected publish process

param(
    [switch]$NoClean,
    [switch]$NoBuild,
    [string]$Output = "MDiceV2_Debug"
)

$ErrorActionPreference = "Stop"
$rootPath = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputPath = Join-Path $rootPath $Output

Write-Host ""
Write-Host "========== MDiceV2 Debug Build (Fixed) ==========" -ForegroundColor Cyan
Write-Host "Root directory: $rootPath"
Write-Host "Output directory: $outputPath"
Write-Host ""

try {
    # Check dotnet installation
    Write-Host "Checking environment..." -ForegroundColor Cyan
    $version = dotnet --version
    Write-Host "[+] dotnet $version" -ForegroundColor Green
    Write-Host ""
    
    # Clean build artifacts (but preserve data)
    if (-not $NoClean) {
        Write-Host "Cleaning build artifacts..." -ForegroundColor Cyan
        
        $cleanPaths = @(
            (Join-Path $rootPath "bin"),
            (Join-Path $rootPath "obj"),
            (Join-Path $rootPath "MDiceV2.Abstractions\bin"),
            (Join-Path $rootPath "MDiceV2.Abstractions\obj"),
            (Join-Path $rootPath "MDiceV2.Interfaces\bin"),
            (Join-Path $rootPath "MDiceV2.Interfaces\obj"),
            (Join-Path $rootPath "MDiceV2.Core\bin"),
            (Join-Path $rootPath "MDiceV2.Core\obj"),
            (Join-Path $rootPath "MDiceV2.Launcher\bin"),
            (Join-Path $rootPath "MDiceV2.Launcher\obj"),
            (Join-Path $rootPath "MDiceV2.Console\bin"),
            (Join-Path $rootPath "MDiceV2.Console\obj"),
            (Join-Path $rootPath "MDiceV2.Tests\bin"),
            (Join-Path $rootPath "MDiceV2.Tests\obj"),
            (Join-Path $rootPath "Mods\CustomizedReply\bin"),
            (Join-Path $rootPath "Mods\CustomizedReply\obj"),
            (Join-Path $rootPath "Mods\AIMod\bin"),
            (Join-Path $rootPath "Mods\AIMod\obj"),
            (Join-Path $rootPath "Mods\ABot\bin"),
            (Join-Path $rootPath "Mods\ABot\obj")
        )
        
        foreach ($path in $cleanPaths) {
            if (Test-Path $path) {
                Remove-Item -Path $path -Recurse -Force -ErrorAction SilentlyContinue | Out-Null
                Write-Host "  [+] Cleaned $(Split-Path -Leaf $path)"
            }
        }
        Write-Host ""
    }
    
    # Build all .NET projects
    if (-not $NoBuild) {
        Write-Host "Building .NET projects..." -ForegroundColor Cyan
        
        $csprojFiles = @(
            (Join-Path $rootPath "MDiceV2.Abstractions\MDiceV2.Abstractions.csproj"),
            (Join-Path $rootPath "MDiceV2.Interfaces\MDiceV2.Interfaces.csproj"),
            (Join-Path $rootPath "MDiceV2.Core\MDiceV2.Core.csproj"),
            (Join-Path $rootPath "MDiceV2.Launcher\MDiceV2.Launcher.csproj"),
            (Join-Path $rootPath "MDiceV2.Console\MDiceV2.Console.csproj"),
            (Join-Path $rootPath "Mods\CustomizedReply\CustomizedReply.csproj"),
            (Join-Path $rootPath "Mods\AIMod\AIMod.csproj"),
            (Join-Path $rootPath "Mods\ABot\ABot.csproj"),
            (Join-Path $rootPath "MDiceV2.Tests\MDiceV2.Tests.csproj")
        )
        
        $successCount = 0
        $failCount = 0
        
        foreach ($csproj in $csprojFiles) {
            if (Test-Path $csproj) {
                $projName = Split-Path (Split-Path $csproj -Parent) -Leaf
                Write-Host "  Building $projName..." -NoNewline
                
                $output = dotnet build $csproj -c Debug --quiet 2>&1
                if ($LASTEXITCODE -eq 0) {
                    Write-Host " [OK]" -ForegroundColor Green
                    $successCount++
                } else {
                    Write-Host " [FAILED]" -ForegroundColor Red
                    $failCount++
                }
            }
        }
        
        Write-Host ""
        Write-Host "Build Result: $successCount succeeded, $failCount failed" -ForegroundColor $(if ($failCount -eq 0) { 'Green' } else { 'Yellow' })
        Write-Host ""
    }
    
    # Publish to Debug directory (key step - FIXED)
    Write-Host "Publishing debug output..." -ForegroundColor Cyan
    
    # Remove old output directory
    if (Test-Path $outputPath) {
        Write-Host "  Removing old output directory..."
        Remove-Item -Path $outputPath -Recurse -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 500
    }
    
    # Publish Launcher (main application)
    Write-Host "  Publishing MDiceV2.Launcher..." -NoNewline
    $launcherProj = Join-Path $rootPath "MDiceV2.Launcher\MDiceV2.Launcher.csproj"
    $publishOutput = dotnet publish $launcherProj -c Debug -o $outputPath --no-build 2>&1
    
    if ($LASTEXITCODE -eq 0) {
        Write-Host " [OK]" -ForegroundColor Green
        
        # Verify output
        $exePath = Join-Path $outputPath "MDiceV2.Launcher.exe"
        if (Test-Path $exePath) {
            $exeSize = [math]::Round((Get-Item $exePath).Length / 1MB, 2)
            Write-Host "    Generated: MDiceV2.Launcher.exe ($exeSize MB)" -ForegroundColor Green
        }
    } else {
        Write-Host " [FAILED]" -ForegroundColor Red
        Write-Host "    $publishOutput" -ForegroundColor Red
        throw "Failed to publish Launcher"
    }
    
    Write-Host ""
    Write-Host "[+] Debug build completed successfully!" -ForegroundColor Green
    Write-Host ""
    Write-Host "To run the debug version:" -ForegroundColor Cyan
    Write-Host "   .\$Output\MDiceV2.Launcher.exe" -ForegroundColor Yellow
    Write-Host ""
    
} catch {
    Write-Host ""
    Write-Host "[ERROR] $_" -ForegroundColor Red
    exit 1
}
