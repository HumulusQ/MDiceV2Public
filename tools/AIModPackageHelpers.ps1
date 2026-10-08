Set-StrictMode -Version Latest

$script:AIModPackageRequiredFiles = @(
    "mod.json",
    "AIMod.dll"
)

$script:AIModPackageOptionalFiles = @(
    "ai-config.json",
    "AIMod.pdb"
)

$script:AIModPackageOptionalDirectories = @(
    "Assets",
    "Data"
)

$script:AIModForbiddenFilePatterns = @(
    "*.deps.json",
    "*.runtimeconfig.json",
    "*.pdb",
    "Avalonia*.dll",
    "Semi.Avalonia*.dll",
    "ReactiveUI*.dll",
    "Splat*.dll",
    "SkiaSharp*.dll",
    "HarfBuzzSharp*.dll",
    "System.Data.SQLite*.dll",
    "Polly*.dll",
    "Grpc*.dll",
    "Google.Protobuf*.dll",
    "protobuf-net*.dll",
    "Microsoft.*.dll",
    "System.*.dll",
    "MDiceV2.Core.dll",
    "MDiceV2.Interfaces.dll",
    "MDiceV2.Abstractions.dll"
)

function Resolve-AIModBuildOutputDir {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BuildPath
    )

    if (-not (Test-Path $BuildPath)) {
        throw "AIMod build output directory not found: $BuildPath"
    }

    if (Test-Path (Join-Path $BuildPath "AIMod.dll") -PathType Leaf) {
        return (Resolve-Path $BuildPath).Path
    }

    $candidateDirs = Get-ChildItem -Path $BuildPath -Directory -ErrorAction SilentlyContinue |
        Sort-Object Name

    foreach ($candidateDir in $candidateDirs) {
        $candidateDll = Join-Path $candidateDir.FullName "AIMod.dll"
        if (Test-Path $candidateDll -PathType Leaf) {
            return $candidateDir.FullName
        }
    }

    throw "AIMod.dll not found under build output: $BuildPath"
}

function Copy-AIModSlimPackageContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ModSourceDir,

        [Parameter(Mandatory = $true)]
        [string]$ModBuildDir,

        [Parameter(Mandatory = $true)]
        [string]$DestinationDir
    )

    $resolvedBuildDir = Resolve-AIModBuildOutputDir -BuildPath $ModBuildDir
    $null = New-Item -ItemType Directory -Path $DestinationDir -Force

    $modJsonPath = Join-Path $ModSourceDir "mod.json"
    if (-not (Test-Path $modJsonPath -PathType Leaf)) {
        throw "AIMod source mod.json not found: $modJsonPath"
    }

    $aiModDllPath = Join-Path $resolvedBuildDir "AIMod.dll"
    if (-not (Test-Path $aiModDllPath -PathType Leaf)) {
        throw "AIMod.dll not found in build output: $aiModDllPath"
    }

    Copy-Item -Path $modJsonPath -Destination (Join-Path $DestinationDir "mod.json") -Force
    Copy-Item -Path $aiModDllPath -Destination (Join-Path $DestinationDir "AIMod.dll") -Force

    foreach ($optionalFile in $script:AIModPackageOptionalFiles) {
        $optionalSourcePath = Join-Path $ModSourceDir $optionalFile
        if (Test-Path $optionalSourcePath -PathType Leaf) {
            Copy-Item -Path $optionalSourcePath -Destination (Join-Path $DestinationDir $optionalFile) -Force
        }
    }

    foreach ($optionalDirectory in $script:AIModPackageOptionalDirectories) {
        $optionalSourceDirectory = Join-Path $ModSourceDir $optionalDirectory
        if (Test-Path $optionalSourceDirectory -PathType Container) {
            Copy-Item -Path $optionalSourceDirectory -Destination $DestinationDir -Recurse -Force
        }
    }

    Test-AIModSlimPackageContent -PackageDir $DestinationDir
}

function Test-AIModSlimPackageContent {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackageDir
    )

    foreach ($requiredFile in $script:AIModPackageRequiredFiles) {
        $requiredPath = Join-Path $PackageDir $requiredFile
        if (-not (Test-Path $requiredPath -PathType Leaf)) {
            throw "AIMod slim package missing required file: $requiredPath"
        }
    }

    $forbiddenArtifacts = [System.Collections.Generic.List[string]]::new()

    $runtimeDir = Join-Path $PackageDir "runtimes"
    if (Test-Path $runtimeDir -PathType Container) {
        $forbiddenArtifacts.Add("runtimes/")
    }

    $allFiles = Get-ChildItem -Path $PackageDir -Recurse -File -ErrorAction Stop
    foreach ($file in $allFiles) {
        if ($file.Name -ieq "AIMod.dll" -or $file.Name -ieq "AIMod.pdb") {
            continue
        }

        if ($file.Name -like "*.dll") {
            $forbiddenArtifacts.Add($file.Name)
            continue
        }

        foreach ($pattern in $script:AIModForbiddenFilePatterns) {
            if ($file.Name -like $pattern) {
                $forbiddenArtifacts.Add($file.Name)
                break
            }
        }
    }

    if ($forbiddenArtifacts.Count -gt 0) {
        $names = $forbiddenArtifacts |
            Sort-Object -Unique |
            Select-Object -First 12
        throw "AIMod slim package contains forbidden runtime artifacts: $($names -join ', ')"
    }
}
