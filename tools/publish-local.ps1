[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "MDiceV2_Published"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$outputPath = Join-Path $root $Output

Write-Host ""
Write-Host "========== MDiceV2 Local Publish (No GitHub Upload) ==========" -ForegroundColor Cyan
Write-Host "Output: $Output"
Write-Host ""

Push-Location $root
try {
    if (Test-Path $outputPath) {
        Write-Host "Cleaning previous publish output..."
        Remove-Item -Path $outputPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Building MDiceV2.Interfaces..."
    dotnet build "MDiceV2.Interfaces/MDiceV2.Interfaces.csproj" -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "MDiceV2.Interfaces build failed with exit code $LASTEXITCODE"
    }

    Write-Host "Building MDiceV2.Core..."
    dotnet build "MDiceV2.Core/MDiceV2.Core.csproj" -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "MDiceV2.Core build failed with exit code $LASTEXITCODE"
    }

    Write-Host "Publishing Launcher (self-contained)..."
    dotnet publish "MDiceV2.Launcher/MDiceV2.Launcher.csproj" `
        -c $Configuration `
        -r $Runtime `
        -p:PublishDir=$outputPath `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        -p:IsCopyingPublishFiles=true
    
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Write-Host ""
    Write-Host "Fixing runtimeconfig.json..." -ForegroundColor Yellow
    
    # For self-contained apps, runtimeconfig should NOT have includedFrameworks
    # Remove it after publishing
    $runtimeConfigPath = Join-Path $outputPath "MDiceV2.Launcher.runtimeconfig.json"
    if (Test-Path $runtimeConfigPath) {
        $content = Get-Content $runtimeConfigPath -Raw | ConvertFrom-Json
        
        # Remove includedFrameworks if present
        if ($content.runtimeOptions.PSObject.Properties.Name -contains 'includedFrameworks') {
            $content.runtimeOptions.PSObject.Properties.Remove('includedFrameworks')
        }
        
        # Add rollForward for proper runtime selection
        if (-not $content.runtimeOptions.PSObject.Properties.Name.Contains('rollForward')) {
            $content.runtimeOptions | Add-Member -MemberType NoteProperty -Name "rollForward" -Value "LatestMinor" -Force
        }
        
        # Save corrected config
        $content | ConvertTo-Json | Set-Content $runtimeConfigPath
        Write-Host "[+] Fixed runtimeconfig.json for self-contained"
    }
    
    
    Write-Host ""
    Write-Host "Organizing dependencies..." -ForegroundColor Yellow
    
    # Create CoreLibs directory for NuGet dependencies
    $coreLibsDir = Join-Path $outputPath "CoreLibs"
    New-Item -ItemType Directory -Path $coreLibsDir -Force -ErrorAction SilentlyContinue | Out-Null
    
    # Copy Core.dll's NuGet dependencies from its build output to CoreLibs
    Write-Host "Copying Core.dll's dependencies from build output..."
    $coreBinPath = Resolve-Path "MDiceV2.Core\bin\$Configuration\net10.0-windows\$Runtime" -ErrorAction SilentlyContinue
    if ($coreBinPath -and (Test-Path $coreBinPath)) {
        Get-ChildItem -Path $coreBinPath -Filter "*.dll" -File | ForEach-Object {
            # Skip System, Microsoft, and MDiceV2 assemblies
            if (-not ($_.Name -like "System.*" -or $_.Name -like "Microsoft.*" -or $_.Name -like "MDiceV2*" -or $_.Name -eq "mscorlib.dll")) {
                $destPath = Join-Path $coreLibsDir $_.Name
                if (-not (Test-Path $destPath)) {
                    Copy-Item -Path $_.FullName -Destination $destPath -Force -ErrorAction SilentlyContinue
                    Write-Host "[+] Copied $($_.Name) to CoreLibs"
                }
            }
        }
    }
    
    # Move any remaining non-system DLLs from root to CoreLibs
    Write-Host "Organizing remaining dependencies..."
    
    # DLLs that must stay in the root directory
    $rootDlls = @(
        'MDiceV2.Core.dll',
        'MDiceV2.Launcher.dll',
        'MDiceV2.Abstractions.dll',
        'MDiceV2.Interfaces.dll'
    )
    
    # List of system/runtime DLLs and files that should stay in root
    $systemDllPatterns = @(
        'System.', 'Microsoft.', 'netstandard', 'mscorlib',
        'coreclr', 'clr', 'hostfxr', 'hostpolicy', 'clrjit'
    )
    
    # Move non-root, non-system DLLs to CoreLibs
    Get-ChildItem -Path $outputPath -Filter "*.dll" -File | ForEach-Object {
        $isRoot = $rootDlls -contains $_.Name
        $isSystem = $false
        
        if (-not $isRoot) {
            foreach ($pattern in $systemDllPatterns) {
                if ($_.Name -like "$pattern*") {
                    $isSystem = $true
                    break
                }
            }
        }
        
        # Move to CoreLibs if not root and not system
        if (-not $isRoot -and -not $isSystem) {
            $destPath = Join-Path $coreLibsDir $_.Name
            if (-not (Test-Path $destPath)) {
                Move-Item -Path $_.FullName -Destination $coreLibsDir -Force -ErrorAction SilentlyContinue
                Write-Host "[+] Moved $($_.Name) to CoreLibs"
            }
        }
    }
    
    Write-Host "[+] Dependency organization complete"
    
    Write-Host ""
    Write-Host "Cleaning up unnecessary files..." -ForegroundColor Yellow
    
    # PublishSingleFile=false, so we get all System.dll files
    # These are needed for the application to run
    # Only remove truly unnecessary files
    
    # Remove the deps folder if it exists (not needed)
    $depsDir = Join-Path $outputPath "deps"
    if (Test-Path $depsDir) {
        Remove-Item -Path $depsDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    # Remove debug utilities and helper tools
    Get-ChildItem -Path $outputPath -Filter "createdump.exe" -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    
    # Remove native subfolder if it exists
    $nativeDir = Join-Path $outputPath "native"
    if (Test-Path $nativeDir) {
        Remove-Item -Path $nativeDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    # Remove runtimes folder if it exists
    $runtimesDir = Join-Path $outputPath "runtimes"
    if (Test-Path $runtimesDir) {
        Remove-Item -Path $runtimesDir -Recurse -Force -ErrorAction SilentlyContinue
    }
    
    # Remove PDB symbol files
    Get-ChildItem -Path $outputPath -Filter "*.pdb" -File -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    
    # Remove unnecessary .json files (keep runtimeconfig and deps.json which are required)
    Get-ChildItem -Path $outputPath -Filter "*.json" -File -ErrorAction SilentlyContinue | 
        Where-Object { $_.Name -ne "MDiceV2.Launcher.runtimeconfig.json" -and $_.Name -ne "MDiceV2.Launcher.deps.json" } |
        Remove-Item -Force -ErrorAction SilentlyContinue
    
    # Ensure mods is a proper directory, not a file
    # (In case the build output accidentally created a mods file instead of folder)
    $modsPath = Join-Path $outputPath "mods"
    if (Test-Path $modsPath) {
        $modsItem = Get-Item -Path $modsPath -Force
        if (-not $modsItem.PSIsContainer) {
            # mods is a file, not a directory - delete it and recreate as folder
            Remove-Item -Path $modsPath -Force -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Path $modsPath -Force -ErrorAction SilentlyContinue | Out-Null
        }
    } else {
        # mods doesn't exist, create empty folder for users to put mod files in
        New-Item -ItemType Directory -Path $modsPath -Force -ErrorAction SilentlyContinue | Out-Null
    }
    
    Write-Host "[+] Cleanup complete"
    Write-Host ""
    
    Write-Host "========== Publish Complete ==========" -ForegroundColor Green
    Write-Host "Publish path: $(Resolve-Path $outputPath)"
    Write-Host ""
    Write-Host "Published files:"
    Get-ChildItem -Path $outputPath -Recurse -File | 
        Select-Object @{N="Relative Path";E={$_.FullName.Replace($outputPath, '').TrimStart('\')}}, @{N="Size (MB)";E={[math]::Round($_.Length/1MB,2)}} |
        Format-Table -AutoSize
    
    Write-Host "[+] Local publish successful!"
    Write-Host ""
    exit 0
}
catch {
    Write-Host ""
    Write-Host "========== Publish Failed ==========" -ForegroundColor Red
    Write-Host "Error: $_"
    Write-Host ""
    exit 1
}
finally {
    Pop-Location
}
