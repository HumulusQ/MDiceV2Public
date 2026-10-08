# MDiceV2 GitHub 发布脚本 - 上传 Core.Zip、发布包 Zip 和 Mods
# 使用方式: .\tools\publish.ps1 [选项]
# 选项: -SkipRelease (仅本地发布，不上传到GitHub)
#       -KeepReleases <N> (上传成功后保留最新 N 个已发布 Release，默认 10)
#       -SkipReleaseCleanup (本次不整理历史 Release)
#       -ReleaseCleanupOnly (仅整理历史 Release，不构建/上传)
#       -PreviewReleaseCleanup (配合 -ReleaseCleanupOnly，仅列出待删除项)

[CmdletBinding()]
param(
    [string]$Owner = "HumulusQ",
    [string]$Repo = "MDiceV2Public",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "MDiceV2_Published",
    [string]$CocCardSource = "",
    [ValidateRange(1, 1000)]
    [int]$KeepReleases = 10,
    [switch]$SkipReleaseCleanup,
    [switch]$ReleaseCleanupOnly,
    [switch]$PreviewReleaseCleanup,
    [switch]$SkipRelease
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "AIModPackageHelpers.ps1")

function Get-GitHubToken {
    function Normalize-Token {
        param([string]$Value)

        if ([string]::IsNullOrWhiteSpace($Value)) {
            return ""
        }

        $clean = $Value.Trim()
        $clean = $clean -replace "^\uFEFF", ""

        if ($clean -match "^(token|bearer)\s+(.+)$") {
            $clean = $Matches[2].Trim()
        }

        if ($clean -match "^[A-Za-z_]+\s*=\s*(.+)$") {
            $clean = $Matches[1].Trim()
        }

        $clean = $clean.Trim('"', "'")
        return $clean
    }

    $token = $env:GITHUB_TOKEN
    if ([string]::IsNullOrWhiteSpace($token)) {
        $token = $env:GH_TOKEN
    }

    $token = Normalize-Token -Value $token

    if ([string]::IsNullOrWhiteSpace($token)) {
        $tokenFile = Join-Path (Join-Path $PSScriptRoot "..") "token.txt"
        if (Test-Path $tokenFile -PathType Leaf) {
            $lines = Get-Content -Path $tokenFile -Encoding utf8
            $line = $lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and -not $_.Trim().StartsWith("#") } | Select-Object -First 1
            $token = Normalize-Token -Value $line
        }
    }

    if ([string]::IsNullOrWhiteSpace($token) -and (-not $SkipRelease.IsPresent -or $ReleaseCleanupOnly.IsPresent)) {
        throw "Set GITHUB_TOKEN/GH_TOKEN or create token.txt with your GitHub Personal Access Token"
    }

    return $token
}

function Get-ApiHeaders {
    param([string]$Token)

    $headers = @{
        "User-Agent" = "MDiceV2-Publish"
        "Accept" = "application/vnd.github+json"
    }

    if (-not [string]::IsNullOrWhiteSpace($Token)) {
        if ($Token.StartsWith("github_pat_", [System.StringComparison]::OrdinalIgnoreCase)) {
            $headers["Authorization"] = "Bearer $Token"
        } else {
            $headers["Authorization"] = "token $Token"
        }
    }

    return $headers
}

function Resolve-CocCardSource {
    param(
        [string]$Root,
        [string]$Source
    )

    if (-not [string]::IsNullOrWhiteSpace($Source)) {
        if (-not (Test-Path $Source -PathType Leaf)) {
            throw "CoC card source file not found: $Source"
        }
        return (Resolve-Path $Source).Path
    }

    $candidate = Get-ChildItem -Path $Root -File -Filter "TOTT_portable_CoC7e_investigator_v*.html" |
        ForEach-Object {
            if ($_.Name -match '^TOTT_portable_CoC7e_investigator_v(?<version>\d+)\.html$') {
                [PSCustomObject]@{ File = $_; Version = [long]$Matches.version }
            }
        } |
        Sort-Object -Property Version -Descending |
        Select-Object -First 1

    if ($null -eq $candidate) {
        throw "No CoC card source matching TOTT_portable_CoC7e_investigator_v<version>.html was found in $Root"
    }

    Write-Host "Using CoC card source: $($candidate.File.Name) (v$($candidate.Version))"
    return $candidate.File.FullName
}

function Test-CocCardAssetName {
    param([string]$Name)

    return $Name -eq "portable_CoC7e_charactercard.html" -or
        $Name -match '^TOTT_portable_CoC7e_investigator_v\d+\.html$'
}

function Get-NextPackageName {
    param(
        [string]$Owner,
        [string]$Repo,
        [string]$Token
    )

    $headers = Get-ApiHeaders -Token $Token
    $releases = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases" -Headers $headers -Method Get

    $numbers = @()
    foreach ($release in $releases) {
        if ($release.name -match "^UpdatePackageV(\d+)$") {
            $numbers += [int]$Matches[1]
        }
    }

    $max = ($numbers | Measure-Object -Maximum).Maximum
    if (-not $max) { $max = 0 }
    $next = $max + 1

    return "UpdatePackageV$next"
}

function Get-OrCreateRelease {
    param(
        [string]$Owner,
        [string]$Repo,
        [string]$Token,
        [string]$Tag,
        [string]$Name,
        [string]$Body
    )

    $headers = Get-ApiHeaders -Token $Token
    $payload = @{ tag_name = $Tag; name = $Name; body = $Body; draft = $false; prerelease = $false } | ConvertTo-Json

    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases" -Headers $headers -Method Post -Body $payload
        return $release
    }
    catch {
        if ($_.Exception.Response.StatusCode.Value__ -eq 422) {
            # Release already exists, update it
            $existing = Invoke-RestMethod -Uri "https://api.github.com/repos/$Owner/$Repo/releases/tags/$Tag" -Headers $headers -Method Get
            $updatePayload = @{ name = $Name; body = $Body; draft = $false; prerelease = $false } | ConvertTo-Json
            $null = Invoke-RestMethod -Uri $existing.url -Headers $headers -Method Patch -Body $updatePayload
            return $existing
        }

        throw
    }
}

function Remove-OldGitHubReleases {
    param(
        [string]$Owner,
        [string]$Repo,
        [string]$Token,
        [ValidateRange(1, 1000)]
        [int]$KeepCount,
        [long]$CurrentReleaseId,
        [switch]$Preview
    )

    $headers = Get-ApiHeaders -Token $Token
    $allReleases = @()
    $page = 1

    while ($true) {
        $rawPageItems = Invoke-RestMethod `
            -Uri "https://api.github.com/repos/$Owner/$Repo/releases?per_page=100&page=$page" `
            -Headers $headers `
            -Method Get
        $pageItems = @($rawPageItems)
        $allReleases += $pageItems

        if ($pageItems.Count -lt 100) {
            break
        }
        $page++
    }

    # Drafts are intentionally excluded: they are unpublished work, not old
    # public releases. ISO-8601 published_at values sort chronologically.
    $publishedReleases = @($allReleases |
        Where-Object { -not $_.draft -and -not [string]::IsNullOrWhiteSpace([string]$_.published_at) } |
        Sort-Object -Property @{ Expression = { [DateTimeOffset]$_.published_at }; Descending = $true }, @{ Expression = { [long]$_.id }; Descending = $true })

    if ($CurrentReleaseId -eq 0) {
        if ($publishedReleases.Count -eq 0) {
            Write-Host "Release cleanup: no published releases to delete."
            return
        }
        $CurrentReleaseId = [long]$publishedReleases[0].id
    }

    $currentRelease = $publishedReleases |
        Where-Object { [long]$_.id -eq $CurrentReleaseId } |
        Select-Object -First 1
    if ($null -eq $currentRelease) {
        throw "Safety check failed: current release $CurrentReleaseId was not returned by the GitHub releases API"
    }

    # An existing tag can be republished without changing published_at. Always
    # count the release uploaded in this run as the first retained release.
    $orderedForRetention = @($currentRelease) + @($publishedReleases |
        Where-Object { [long]$_.id -ne $CurrentReleaseId })

    if ($orderedForRetention.Count -le $KeepCount) {
        Write-Host "Release cleanup: $($publishedReleases.Count) published release(s), nothing older than newest $KeepCount to delete."
        return
    }

    $oldReleases = @($orderedForRetention | Select-Object -Skip $KeepCount |
        Sort-Object -Property @{ Expression = { [DateTimeOffset]$_.published_at }; Descending = $false }, @{ Expression = { [long]$_.id }; Descending = $false })
    Write-Host "Release cleanup: keeping newest $KeepCount and deleting $($oldReleases.Count) older release(s)..."

    foreach ($oldRelease in $oldReleases) {
        if ([long]$oldRelease.id -eq $CurrentReleaseId) {
            throw "Safety check failed: current release $CurrentReleaseId was selected for deletion"
        }

        $label = if (-not [string]::IsNullOrWhiteSpace([string]$oldRelease.name)) {
            [string]$oldRelease.name
        } else {
            [string]$oldRelease.tag_name
        }
        Write-Host "  Deleting old release: $label (published $($oldRelease.published_at), id $($oldRelease.id))"
        if ($Preview.IsPresent) {
            Write-Host "  Preview only: would delete release id $($oldRelease.id)."
            continue
        }
        Invoke-RestMethod `
            -Uri "https://api.github.com/repos/$Owner/$Repo/releases/$($oldRelease.id)" `
            -Headers $headers `
            -Method Delete | Out-Null
    }

    if ($Preview.IsPresent) {
        Write-Host "✓ Release cleanup preview complete; nothing deleted."
    } else {
        Write-Host "✓ Release cleanup complete"
    }
}

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
if ($PreviewReleaseCleanup.IsPresent -and -not $ReleaseCleanupOnly.IsPresent) {
    throw "-PreviewReleaseCleanup requires -ReleaseCleanupOnly"
}
if ($ReleaseCleanupOnly.IsPresent -and ($SkipRelease.IsPresent -or $SkipReleaseCleanup.IsPresent)) {
    throw "-ReleaseCleanupOnly cannot be combined with -SkipRelease or -SkipReleaseCleanup"
}
$token = Get-GitHubToken
if ($ReleaseCleanupOnly.IsPresent) {
    Remove-OldGitHubReleases -Owner $Owner -Repo $Repo -Token $token -KeepCount $KeepReleases -Preview:$PreviewReleaseCleanup
    return
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $root $Output))
$rootPrefix = ([string]$root).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $outputPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Output directory must be a subdirectory of the project: $outputPath"
}

$CocCardSource = Resolve-CocCardSource -Root $root -Source $CocCardSource
$cocCardAssetName = Split-Path -Path $CocCardSource -Leaf

Push-Location $root
try {
    # Clean output directory
    if (Test-Path $outputPath) {
        Write-Host "Cleaning previous publish output..."
        Remove-Item -LiteralPath $outputPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Build Interfaces and Core
    Write-Host "Building MDiceV2.Interfaces..."
    dotnet build "MDiceV2.Interfaces/MDiceV2.Interfaces.csproj" -c $Configuration --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "Building Interfaces failed"
    }

    Write-Host "Building MDiceV2.Core..."
    dotnet build "MDiceV2.Core/MDiceV2.Core.csproj" -c $Configuration --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "Building Core failed"
    }

    # Build CustomizedReply Mod
    Write-Host "Building CustomizedReply Mod..."
    dotnet build "Mods/CustomizedReply/CustomizedReply.csproj" -c $Configuration --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "Building CustomizedReply Mod failed"
    }

    # Build AIMod
    Write-Host "Building AIMod..."
    dotnet build "Mods/AIMod/AIMod.csproj" -c $Configuration --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "Building AIMod failed"
    }

    # Step 1: Publish Launcher to output root
    Write-Host "Publishing Launcher to root..."
    dotnet publish "MDiceV2.Launcher/MDiceV2.Launcher.csproj" -c $Configuration -r $Runtime -o "$outputPath" --no-restore --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing Launcher failed"
    }

    # Step 2: Publish Console to output root (lightweight launcher - uses system runtime)
    Write-Host "Publishing Console to root..."
    # Console is a lightweight launcher that uses system .NET runtime (not self-contained)
    # Need to allow restore to update project.assets.json with correct RuntimeIdentifier
    dotnet publish "MDiceV2.Console/MDiceV2.Console.csproj" -c $Configuration -r $Runtime -o "$outputPath" --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing Console failed"
    }

    # Step 3: Publish Core to Core subdirectory (self-contained single file per .csproj configuration)
    # Note: Core is configured as self-contained single file in .csproj and is loaded by Launcher
    $coreOutputPath = Join-Path $outputPath "Core"
    
    # 杀死任何正在运行的 Core.Dice 进程（避免文件锁定问题）
    Write-Host "Checking for running Core processes..."
    $coreProcesses = Get-Process -Name "MDiceV2.Core.Dice" -ErrorAction SilentlyContinue
    if ($coreProcesses) {
        Write-Host "Killing running Core.Dice processes..."
        $coreProcesses | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1  # 等待进程完全释放文件
    }
    
    Write-Host "Publishing Core to Core subdirectory..."
    dotnet publish "MDiceV2.Core/MDiceV2.Core.csproj" -c $Configuration -r $Runtime -o "$coreOutputPath" --no-restore --disable-build-servers -m:1
    if ($LASTEXITCODE -ne 0) {
        throw "Publishing Core failed"
    }

    # Step 3.5: Copy mods to the output directory
    Write-Host "Copying mods to output directory..."
    $modsSourceDir = Join-Path $root "Mods"
    $modsOutputDir = Join-Path $outputPath "mods"
    
    if (Test-Path $modsSourceDir) {
        # Create mods output directory
        $null = New-Item -ItemType Directory -Path $modsOutputDir -Force
        
        # Copy each mod
        Get-ChildItem -Path $modsSourceDir -Directory | Where-Object { $_.Name -notmatch '^\.' } | ForEach-Object {
            $modName = $_.Name
            $modBuildDir = Join-Path $_.FullName "bin\$Configuration\net10.0-windows"
            
            if (Test-Path $modBuildDir) {
                $modTargetDir = Join-Path $modsOutputDir $modName
                $null = New-Item -ItemType Directory -Path $modTargetDir -Force

                if ($modName -eq "AIMod") {
                    Copy-AIModSlimPackageContent -ModSourceDir $_.FullName -ModBuildDir $modBuildDir -DestinationDir $modTargetDir
                    Write-Host "  Copied AIMod slim package"
                } else {
                    Copy-Item -Path "$modBuildDir\*" -Destination $modTargetDir -Recurse -Force
                    Write-Host "  Copied mod runtime output: $modName"

                    # Copy mod.json if exists
                    $modJson = Join-Path $_.FullName "mod.json"
                    if (Test-Path $modJson) {
                        Copy-Item -Path $modJson -Destination $modTargetDir -Force
                    }

                    # Copy data.json if exists (旧路径，向后兼容)
                    $dataJson = Join-Path $_.FullName "data.json"
                    if (Test-Path $dataJson) {
                        Copy-Item -Path $dataJson -Destination $modTargetDir -Force
                    }
                }
            } else {
                Write-Host "  Warning: Mod build directory not found for $modName at $modBuildDir"
            }
        }
    }
    
    # Step 3.6: Copy data/[modname]/ directories to output root
    Write-Host "Copying mod data directories..."
    $dataSource = Join-Path $root "data"
    if (Test-Path $dataSource) {
        # Copy each mod's data directory
        $dataOutputDir = Join-Path $outputPath "data"
        $null = New-Item -ItemType Directory -Path $dataOutputDir -Force -ErrorAction SilentlyContinue
        
        Get-ChildItem -Path $dataSource -Directory | ForEach-Object {
            $dirName = $_.Name
            $dirTarget = Join-Path $dataOutputDir $dirName
            $null = New-Item -ItemType Directory -Path $dirTarget -Force -ErrorAction SilentlyContinue
            Copy-Item -Path "$($_.FullName)\*" -Destination $dirTarget -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "  Copied data/$dirName to output"
        }
    }

    # Step 4: Organize dependencies - move duplicate/non-essential DLLs to deps/ subdirectory
    Write-Host "Organizing dependencies..."
    $depsPath = Join-Path $outputPath "deps"
    
    # Core-related DLLs that must stay in root
    $coreDllNames = @("MDiceV2.Launcher.dll", "MDiceV2.Console.dll", "MDiceV2.Core.dll", "MDiceV2.Abstractions.dll", "MDiceV2.Interfaces.dll")
    
    # Create deps directory for root (but NOT for Core - Core keeps all libs together)
    $null = New-Item -ItemType Directory -Path $depsPath -Force -ErrorAction SilentlyContinue
    
    # Move non-essential DLLs from root to deps/
    Get-ChildItem -Path $outputPath -Filter "*.dll" -File | Where-Object { 
        $name = $_.Name
        $coreDllNames -notcontains $name
    } | ForEach-Object {
        Write-Host "  Moving to deps/: $($_.Name)"
        Move-Item -Path $_.FullName -Destination $depsPath -Force -ErrorAction SilentlyContinue
    }
    
    # For Core: keep ALL DLLs in Core directory (don't move to Core/deps/)
    # This ensures all Avalonia and other UI framework dependencies are available
    Write-Host "  Core directory: keeping all DLLs together for Avalonia framework"

    # Verify Core.Dice file exists (MSBuild Target RenameOutputToGameExe already handles renaming)
    $coreDiceFile = Get-ChildItem -Path $coreOutputPath -Filter "MDiceV2.Core.Dice" -File | Select-Object -First 1
    if ($coreDiceFile) {
        Write-Host "Core published as: $($coreDiceFile.Name)"
    } else {
        throw "Core.Dice file not found in $coreOutputPath - publish may have failed"
    }

    # Clean PDB files
    Get-ChildItem -Path $outputPath -Filter "*.pdb" -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

    # Read build counter for version (需要在打包之前定义)
    $buildCounterFile = Join-Path $root "build.counter"
    if (-not (Test-Path $buildCounterFile)) {
        throw "build.counter file not found"
    }
    $buildCounter = (Get-Content $buildCounterFile).Trim()
    if ($buildCounter -notmatch '^\d+$') {
        throw "build.counter must contain only digits"
    }
    Write-Host "Build counter: $buildCounter"

    # Create releases directory if it doesn't exist
    $releasesPath = Join-Path $root "releases"
    $null = New-Item -ItemType Directory -Path $releasesPath -Force -ErrorAction SilentlyContinue

    # ========== Core 文件打包为 Zip ==========
    Write-Host "Packaging Core files to Zip..."
    
    # 验证 Core.Dice 文件存在（这是打包所有 Core 相关文件的基础）
    $coreDiceFile = Get-ChildItem -Path $coreOutputPath -Filter "MDiceV2.Core.Dice" -File | Select-Object -First 1
    if ($coreDiceFile) {
        Write-Host "Core published as: $($coreDiceFile.Name)"
    } else {
        throw "Core.Dice file not found in $coreOutputPath - publish may have failed"
    }
    
    # 为 Core Zip 创建临时目录
    $coreZipName = "MDiceV2.Core.Zip"
    $coreZipPath = Join-Path $releasesPath $coreZipName
    $coreTempPath = Join-Path ([System.IO.Path]::GetTempPath()) "MDiceV2_Core_$buildCounter"
    
    # 清理旧临时目录
    if (Test-Path $coreTempPath) {
        Remove-Item -Path $coreTempPath -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $coreTempPath -Force
    
    try {
        # 创建 Core 目录结构
        $coreDir = Join-Path $coreTempPath "Core"
        $null = New-Item -ItemType Directory -Path $coreDir -Force
        
        # 复制 Core 输出目录中的所有文件（MDiceV2.Core.Dice + 所有依赖 DLL）
        Write-Host "  Copying Core files to temp directory..."
        Copy-Item -Path "$coreOutputPath/*" -Destination $coreDir -Recurse -Force
        Write-Host "  ✓ Core files copied"
        
        # 删除旧的 Zip 文件
        if (Test-Path $coreZipPath) {
            Remove-Item -Path $coreZipPath -Force
            Write-Host "  Removed old Zip file"
        }
        
        # 创建 Zip
        Write-Host "  Creating zip file..."
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($coreTempPath, $coreZipPath)
        
        # 验证 Zip 文件
        if (Test-Path $coreZipPath) {
            $zipSize = (Get-Item $coreZipPath).Length
            Write-Host "✓ Core packaged to Zip: $coreZipName ($zipSize bytes)"
        } else {
            throw "Failed to create Core Zip file"
        }
    }
    finally {
        # 清理临时目录
        if (Test-Path $coreTempPath) {
            Remove-Item -Path $coreTempPath -Recurse -Force
        }
    }

    # ========== Publish 输出目录打包为 Zip ==========
    Write-Host "Packaging publish output to Zip..."
    $publishZipName = "MDiceV2.PublishV$buildCounter.zip"
    $publishZipPath = Join-Path $releasesPath $publishZipName

    if (Test-Path $publishZipPath) {
        Remove-Item -Path $publishZipPath -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory($outputPath, $publishZipPath)

    if (Test-Path $publishZipPath) {
        $zipSize = (Get-Item $publishZipPath).Length
        Write-Host "✓ Publish output packaged: $publishZipName ($zipSize bytes)"
    } else {
        throw "Failed to create publish output Zip file"
    }

    # Package CustomizedReply Mod to releases folder
    Write-Host "Packaging CustomizedReply Mod..."
    $modBuildPath = Join-Path $root "Mods/CustomizedReply/bin/$Configuration/net10.0-windows"
    $modDllFile = Join-Path $modBuildPath "CustomizedReply.dll"
    
    if (-not (Test-Path $modDllFile)) {
        throw "CustomizedReply.dll not found at $modDllFile"
    }

    # Create mod zip file name (buildCounter already defined above)
    $modZipName = "CustomizedReplyPackV$buildCounter.zip"
    $modZipPath = Join-Path $releasesPath $modZipName
    
    # Create temp directory for mod packaging
    $modTempPath = Join-Path ([System.IO.Path]::GetTempPath()) "MDiceV2_CustomizedReply_$buildCounter"
    if (Test-Path $modTempPath) {
        Remove-Item -Path $modTempPath -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $modTempPath -Force
    
    try {
        # Create mod structure: CustomizedReply/dll + mod.json + data.json
        $modDir = Join-Path $modTempPath "CustomizedReply"
        $null = New-Item -ItemType Directory -Path $modDir -Force
        
        # Copy mod DLL
        Copy-Item -Path $modDllFile -Destination $modDir -Force
        Write-Host "  Copied CustomizedReply.dll"
        
        # Copy mod.json if exists
        $modJsonPath = Join-Path $root "Mods/CustomizedReply/mod.json"
        if (Test-Path $modJsonPath) {
            Copy-Item -Path $modJsonPath -Destination $modDir -Force
            Write-Host "  Copied mod.json"
        }
        
        # Copy data.json if exists
        $dataJsonPath = Join-Path $root "Mods/CustomizedReply/data.json"
        if (Test-Path $dataJsonPath) {
            Copy-Item -Path $dataJsonPath -Destination $modDir -Force
            Write-Host "  Copied data.json"
        }
        
        # Remove old zip if exists
        if (Test-Path $modZipPath) {
            Remove-Item -Path $modZipPath -Force
        }
        
        # Create zip
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($modTempPath, $modZipPath)
        Write-Host "✓ CustomizedReply mod packaged: $modZipName"
    }
    finally {
        # Clean temp directory
        if (Test-Path $modTempPath) {
            Remove-Item -Path $modTempPath -Recurse -Force
        }
    }

    # Package AIMod to releases folder
    Write-Host "Packaging AIMod..."
    $aiModBuildPath = Join-Path $root "Mods/AIMod/bin/$Configuration/net10.0-windows"
    $aiModDllFile = Join-Path $aiModBuildPath "AIMod.dll"

    if (-not (Test-Path $aiModDllFile)) {
        throw "AIMod.dll not found at $aiModDllFile"
    }

    $aiModZipName = "AIModPackV$buildCounter.zip"
    $aiModZipPath = Join-Path $releasesPath $aiModZipName

    $aiModTempPath = Join-Path ([System.IO.Path]::GetTempPath()) "MDiceV2_AIMod_$buildCounter"
    if (Test-Path $aiModTempPath) {
        Remove-Item -Path $aiModTempPath -Recurse -Force
    }
    $null = New-Item -ItemType Directory -Path $aiModTempPath -Force

    try {
        $aiModDir = Join-Path $aiModTempPath "AIMod"
        $null = New-Item -ItemType Directory -Path $aiModDir -Force

        Copy-AIModSlimPackageContent -ModSourceDir (Join-Path $root "Mods/AIMod") -ModBuildDir $aiModBuildPath -DestinationDir $aiModDir
        Write-Host "  Copied AIMod slim package"

        if (Test-Path $aiModZipPath) {
            Remove-Item -Path $aiModZipPath -Force
        }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($aiModTempPath, $aiModZipPath)
        Write-Host "✓ AIMod packaged: $aiModZipName"
    }
    finally {
        if (Test-Path $aiModTempPath) {
            Remove-Item -Path $aiModTempPath -Recurse -Force
        }
    }

    # Verify Core .Dice file exists
    $coreDiceFile = Get-ChildItem -Path $coreOutputPath -Filter "*.Dice" -File | Select-Object -First 1
    if (-not $coreDiceFile) {
        throw "Core .Dice file not found at $coreOutputPath"
    }

    # Get version from Core executable (single file) or DLL (multi-file)
    $coreExePath = Join-Path $coreOutputPath "MDiceV2.Core.Dice"
    $coreDllPath = Join-Path $coreOutputPath "MDiceV2.Core.dll"
    $assemblyVersion = ""
    
    # Try single file executable first
    if (Test-Path $coreExePath) {
        $assemblyVersion = (Get-Item $coreExePath).VersionInfo.ProductVersion
        if (-not $assemblyVersion) {
            $assemblyVersion = (Get-Item $coreExePath).VersionInfo.FileVersion
        }
        if ($assemblyVersion) {
            Write-Host "Version found from Core.Dice: $assemblyVersion"
        }
    }
    
    # Fall back to DLL if executable doesn't have version
    if ([string]::IsNullOrWhiteSpace($assemblyVersion) -and (Test-Path $coreDllPath)) {
        $assemblyVersion = (Get-Item $coreDllPath).VersionInfo.ProductVersion
        if (-not $assemblyVersion) {
            $assemblyVersion = (Get-Item $coreDllPath).VersionInfo.FileVersion
        }
        if ($assemblyVersion) {
            Write-Host "Version found from Core.dll: $assemblyVersion"
        }
    }

    if ([string]::IsNullOrWhiteSpace($assemblyVersion)) {
        Write-Host "Warning: Could not read assembly version from Core executable or DLL, using default"
        $assemblyVersion = "0.0.0.0"
    }

    # GitHub Release (if not skipped)
    if (-not $SkipRelease.IsPresent) {
        if ([string]::IsNullOrWhiteSpace($token)) {
            throw "No GitHub token available"
        }

        Write-Host "Creating GitHub release..."
        $releaseName = Get-NextPackageName -Owner $Owner -Repo $Repo -Token $token
        $releaseTag = $assemblyVersion
        Write-Host "Release: $releaseName (Tag: $releaseTag)"

        $releaseBody = "AssemblyVersion=$assemblyVersion`r`n`r`nIncluded:`r`n  - Launcher.exe (UI entry point)`r`n  - Console.exe (CLI entry point)`r`n  - MDiceV2.Core.Zip (Core application bundle - RECOMMENDED)`r`n  - MDiceV2.PublishV$buildCounter.zip (Full publish output)`r`n  - CustomizedReplyPackV$buildCounter.zip (CustomizedReply Mod)`r`n  - AIModPackV$buildCounter.zip (AIMod slim plugin package)`r`n  - $cocCardAssetName (portable CoC investigator card)`r`nCore runtime dependencies are bundled with the host app; AIMod relies on host-shared libraries."
        $release = Get-OrCreateRelease -Owner $Owner -Repo $Repo -Token $token -Tag $releaseTag -Name $releaseName -Body $releaseBody

        # ========== Upload Publish Zip asset ==========
        Write-Host "Uploading publish output Zip to release..."
        $headers = Get-ApiHeaders -Token $token

        foreach ($asset in ($release.assets | Where-Object { $_.name -like "MDiceV2.PublishV*.zip" })) {
            Invoke-RestMethod -Uri $asset.url -Headers $headers -Method Delete | Out-Null
            Write-Host "  Removed old: $($asset.name)"
        }

        if (Test-Path $publishZipPath) {
            $uploadUrl = $release.upload_url -replace "\{\?name,label\}$", "?name=$publishZipName"
            $uploadHeaders = $headers.Clone()
            $uploadHeaders["Content-Type"] = "application/zip"

            Write-Host "  Uploading $publishZipName..."
            $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method Post -InFile $publishZipPath -UseBasicParsing
            Write-Host "  ✓ Publish output Zip uploaded to release"
        } else {
            Write-Host "  Warning: Publish output Zip not found, skipping upload"
        }

        # ========== Upload Core.Zip asset (优先上传新的 Zip 格式) ==========
        Write-Host "Uploading MDiceV2.Core.Zip to release..."
        
        # 删除旧的 Core.Zip 资源
        foreach ($asset in ($release.assets | Where-Object { $_.name -like "*Core.Zip*" })) {
            Invoke-RestMethod -Uri $asset.url -Headers $headers -Method Delete | Out-Null
            Write-Host "  Removed old: $($asset.name)"
        }
        
        # 上传新的 Core.Zip
        $coreZipPath = Join-Path $releasesPath "MDiceV2.Core.Zip"
        if (Test-Path $coreZipPath) {
            $uploadUrl = $release.upload_url -replace "\{\?name,label\}$", "?name=MDiceV2.Core.Zip"
            $uploadHeaders = $headers.Clone()
            $uploadHeaders["Content-Type"] = "application/zip"
            
            Write-Host "  Uploading Core.Zip..."
            $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method Post -InFile $coreZipPath -UseBasicParsing
            Write-Host "  ✓ MDiceV2.Core.Zip uploaded to release"
        } else {
            Write-Host "  Warning: MDiceV2.Core.Zip not found, skipping upload"
        }

        # Upload CustomizedReply Mod asset
        Write-Host "Uploading CustomizedReply Mod to release..."
        
        # Delete old CustomizedReply assets
        foreach ($asset in ($release.assets | Where-Object { $_.name -like "*CustomizedReplyPack*" })) {
            Invoke-RestMethod -Uri $asset.url -Headers $headers -Method Delete | Out-Null
            Write-Host "Removed old: $($asset.name)"
        }
        
        # Upload new CustomizedReply mod zip
        $modZipPath = Join-Path $releasesPath $modZipName
        if (Test-Path $modZipPath) {
            $uploadUrl = $release.upload_url -replace "\{\?name,label\}$", "?name=$modZipName"
            $uploadHeaders = $headers.Clone()
            $uploadHeaders["Content-Type"] = "application/zip"
            
            $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method Post -InFile $modZipPath -UseBasicParsing
            Write-Host "✓ CustomizedReply Mod uploaded to release"
        }

        # Upload AIMod asset
        Write-Host "Uploading AIMod to release..."

        foreach ($asset in ($release.assets | Where-Object { $_.name -like "*AIModPack*" })) {
            Invoke-RestMethod -Uri $asset.url -Headers $headers -Method Delete | Out-Null
            Write-Host "Removed old: $($asset.name)"
        }

        $aiModZipPath = Join-Path $releasesPath $aiModZipName
        if (Test-Path $aiModZipPath) {
            $uploadUrl = $release.upload_url -replace "\{\?name,label\}$", "?name=$aiModZipName"
            $uploadHeaders = $headers.Clone()
            $uploadHeaders["Content-Type"] = "application/zip"

            $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method Post -InFile $aiModZipPath -UseBasicParsing
            Write-Host "✓ AIMod uploaded to release"
        }

        # Upload portable CoC investigator card used by #update coccard / .get coccard.
        Write-Host "Uploading portable CoC investigator card..."
        foreach ($asset in ($release.assets | Where-Object { Test-CocCardAssetName -Name $_.name })) {
            Invoke-RestMethod -Uri $asset.url -Headers $headers -Method Delete | Out-Null
            Write-Host "Removed old: $($asset.name)"
        }

        $uploadUrl = $release.upload_url -replace "\{\?name,label\}$", "?name=$cocCardAssetName"
        $uploadHeaders = $headers.Clone()
        $uploadHeaders["Content-Type"] = "text/html; charset=utf-8"
        $null = Invoke-WebRequest -Uri $uploadUrl -Headers $uploadHeaders -Method Post -InFile $CocCardSource -UseBasicParsing
        Write-Host "✓ Portable CoC investigator card uploaded"

        # Only prune history after every asset for the current release has
        # uploaded successfully. A failed upload therefore never removes an
        # older known-good release.
        if ($SkipReleaseCleanup.IsPresent) {
            Write-Host "SkipReleaseCleanup enabled - not deleting historical releases"
        } else {
            Remove-OldGitHubReleases `
                -Owner $Owner `
                -Repo $Repo `
                -Token $token `
                -KeepCount $KeepReleases `
                -CurrentReleaseId ([long]$release.id)
        }
        
        Write-Host "Release URL: $($release.html_url)"
    } else {
        Write-Host "SkipRelease enabled - not uploading to GitHub"
    }

    Write-Host "`n✓ Publish complete!" -ForegroundColor Green
}
finally {
    Pop-Location
}

