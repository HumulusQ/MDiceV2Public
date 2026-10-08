param(
    [Parameter(Mandatory = $true)][string]$AssemblyVersion,
    [Parameter(Mandatory = $true)][string]$InformationalVersion,
    [Parameter(Mandatory = $true)][string]$CounterFile,
    [Parameter(Mandatory = $true)][string]$BaseFile,
    [Parameter(Mandatory = $true)][string]$GeneratedFile
)

$ErrorActionPreference = "Stop"

# Validate mandatory parameters before proceeding
if ([string]::IsNullOrWhiteSpace($AssemblyVersion)) {
    throw "AssemblyVersion parameter is empty or null. This typically means the property was not defined in the project."
}

if ([string]::IsNullOrWhiteSpace($CounterFile) -or [string]::IsNullOrWhiteSpace($BaseFile) -or [string]::IsNullOrWhiteSpace($GeneratedFile)) {
    throw "One or more required file paths are empty: CounterFile='$CounterFile', BaseFile='$BaseFile', GeneratedFile='$GeneratedFile'"
}

# Prevent concurrent builds from racing on build.counter
$mutex = New-Object System.Threading.Mutex($false, "MDiceV2VersionCounter")
$hasHandle = $false

try {
    $hasHandle = $mutex.WaitOne(30000, $false)
    if (-not $hasHandle) {
        throw "Failed to acquire version counter lock within 30s"
    }

    $parts = ($AssemblyVersion -split '\.')
    while ($parts.Count -lt 3) { $parts += 0 }
    $basePrefix = '{0}.{1}.{2}' -f $parts[0], $parts[1], $parts[2]

    $sessionFile = "$CounterFile.session"
    $sessionValid = $false
    if (Test-Path $sessionFile) {
        $sessionContent = Get-Content -Raw -Path $sessionFile
        if (-not [string]::IsNullOrEmpty($sessionContent)) {
            $sessionData = $sessionContent.Trim()
            if ($sessionData -match '^(.*)\|(\d+)$') {
                $sessionBase = $Matches[1]
                $sessionTicks = [int64]$Matches[2]
                $sessionTime = [datetime]::FromFileTimeUtc($sessionTicks)
                if ($sessionBase -eq $basePrefix -and ([datetime]::UtcNow - $sessionTime).TotalMinutes -lt 10) {
                    $sessionValid = $true
                }
            }
        }
    }

    $rev = 0
    $shouldIncrement = $false
    
    if (Test-Path $BaseFile) {
        $baseContent = Get-Content -Raw -Path $BaseFile
        $lastBase = if (-not [string]::IsNullOrEmpty($baseContent)) { $baseContent.Trim() } else { '' }
        
        if ($lastBase -eq $basePrefix -and (Test-Path $CounterFile)) {
            $counterContent = Get-Content -Raw -Path $CounterFile
            if (-not [string]::IsNullOrEmpty($counterContent)) {
                [int]::TryParse($counterContent.Trim(), [ref]$rev) | Out-Null
            }
            $shouldIncrement = -not $sessionValid
        } else {
            # Base changed, start new sequence
            $rev = 0
            $shouldIncrement = $true
        }
    } elseif (Test-Path $CounterFile) {
        $counterContent = Get-Content -Raw -Path $CounterFile
        if (-not [string]::IsNullOrEmpty($counterContent)) {
            [int]::TryParse($counterContent.Trim(), [ref]$rev) | Out-Null
        }
        $shouldIncrement = -not $sessionValid
    } else {
        $shouldIncrement = $true
    }

    # Check if we need to increment: increment on first build of this base in a session
    if ($shouldIncrement) {
        $rev++
    }
    
    $full = '{0}.{1}' -f $basePrefix, $rev

    $infoSuffix = $InformationalVersion
    if (-not $infoSuffix) { $infoSuffix = '' }
    $infoSuffix = $infoSuffix.Trim()
    $infoVersion = $full
    if (-not [string]::IsNullOrWhiteSpace($infoSuffix)) {
        $infoVersion = "$full-$infoSuffix"
    }

    # Write files atomically with validation and backup
    # First, ensure we have a valid state before writing
    if ([string]::IsNullOrWhiteSpace($basePrefix)) {
        throw "CRITICAL: basePrefix is empty or null. This should never happen. AssemblyVersion='$AssemblyVersion', Parts='$($parts -join ',')'"
    }

    if ($rev -lt 0) {
        throw "CRITICAL: Revision number is invalid: $rev"
    }
    
    # Create backup of critical files before writing
    if (Test-Path $BaseFile) {
        $backupFile = "$BaseFile.backup"
        try {
            Copy-Item -Path $BaseFile -Destination $backupFile -Force | Out-Null
        } catch {
            Write-Host "Warning: Could not create backup of $BaseFile" -ForegroundColor Yellow
        }
    }
    
    # Write counter directory
    $counterDir = Split-Path -Parent $CounterFile
    if (-not [string]::IsNullOrEmpty($counterDir)) { New-Item -ItemType Directory -Force -Path $counterDir | Out-Null }
    
    # A previous MSBuild node can briefly retain a handle after its target has
    # completed. Retry the small group of writes while retaining the mutex.
    $writeError = $null
    for ($attempt = 1; $attempt -le 10; $attempt++) {
        try {
            Set-Content -Path $CounterFile -Value $rev -Encoding UTF8 -ErrorAction Stop
            Set-Content -Path $BaseFile -Value $basePrefix -Encoding UTF8 -ErrorAction Stop
            Set-Content -Path $sessionFile -Value "$basePrefix|$([datetime]::UtcNow.ToFileTimeUtc())" -Encoding UTF8 -ErrorAction Stop
            $writeError = $null
            break
        } catch {
            $writeError = $_
            Start-Sleep -Milliseconds 200
        }
    }

    if ($writeError) {
        # If writing failed, attempt to restore the previous base file without
        # treating the intentionally raised failure as a restore failure.
        $restoreError = $null
        if (Test-Path "$BaseFile.backup") {
            try {
                Move-Item -Path "$BaseFile.backup" -Destination $BaseFile -Force -ErrorAction Stop
            } catch {
                $restoreError = $_
            }
        }

        if ($restoreError) {
            throw "CRITICAL: Failed to write version files and restore the backup. Write error: $writeError; restore error: $restoreError"
        }
        throw "Failed to write version files after 10 attempts. Original error: $writeError"
    }

    $generatedDir = Split-Path -Parent $GeneratedFile
    if (-not [string]::IsNullOrEmpty($generatedDir)) { New-Item -ItemType Directory -Force -Path $generatedDir | Out-Null }

    $content = @"
using System.Reflection;
[assembly: AssemblyVersion("$full")]
[assembly: AssemblyFileVersion("$full")]
[assembly: AssemblyInformationalVersion("$infoVersion")]
"@
    $content | Out-File -FilePath $GeneratedFile -Encoding UTF8 -Force

    Write-Host "Generated version $full (info: $infoVersion) -> $(Split-Path -Leaf $GeneratedFile)"
}
finally {
    # Clean up mutex resources safely
    try {
        if ($hasHandle -and $mutex) {
            $mutex.ReleaseMutex()
        }
    } catch {
        # Log but don't throw - we're already in error handling
        Write-Host "Warning: Failed to release mutex: $_" -ForegroundColor Yellow
    }
    
    try {
        if ($mutex) {
            $mutex.Dispose()
        }
    } catch {
        # Log but don't throw - we're already in error handling
        Write-Host "Warning: Failed to dispose mutex: $_" -ForegroundColor Yellow
    }
}
