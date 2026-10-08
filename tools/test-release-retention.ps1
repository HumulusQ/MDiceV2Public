[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'

# Load only the API-header and retention functions, never the build/upload body.
$parseErrors = $null
$tokens = $null
$scriptPath = Join-Path $PSScriptRoot 'publish.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
foreach ($name in @('Get-ApiHeaders', 'Remove-OldGitHubReleases')) {
    $definition = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if ($null -eq $definition) { throw "Missing function: $name" }
    Invoke-Expression $definition.Extent.Text
}

function New-TestRelease([long]$Id, [string]$Date, [bool]$Draft = $false) {
    [pscustomobject]@{ id = $Id; name = "Release$Id"; tag_name = "v$Id"; published_at = $Date; draft = $Draft }
}

function Invoke-RestMethod {
    param([string]$Uri, $Headers, [string]$Method)
    if ($Method -eq 'Get') {
        $script:Reads++
        if ($Uri -notmatch '[?&]page=(\d+)') { throw "Unexpected request: $Uri" }
        return ,$script:MockPages[[int]$Matches[1] - 1]
    }
    if ($Method -eq 'Delete' -and $Uri -match '/releases/(\d+)$') {
        $script:Deleted.Add([long]$Matches[1])
        return
    }
    throw "Unexpected request: $Method $Uri"
}

function Set-TestPages($Pages) {
    $script:MockPages = $Pages
    $script:Deleted = [Collections.Generic.List[long]]::new()
    $script:Reads = 0
}

function Assert-Ids([long[]]$Expected) {
    if (($script:Deleted -join ',') -ne ($Expected -join ',')) {
        throw "Expected deletes [$($Expected -join ',')], got [$($script:Deleted -join ',')]"
    }
}

$releases = @(
    (New-TestRelease 5 '2026-10-05T00:00:00Z'),
    (New-TestRelease 2 '2026-10-02T00:00:00Z'),
    (New-TestRelease 99 '' $true),
    (New-TestRelease 4 '2026-10-04T00:00:00Z'),
    (New-TestRelease 1 '2026-10-01T00:00:00Z'),
    (New-TestRelease 3 '2026-10-03T00:00:00Z')
)
Set-TestPages @(,$releases)
Remove-OldGitHubReleases -Owner test -Repo test -Token fake -KeepCount 2 -CurrentReleaseId 5
Assert-Ids @(1, 2, 3) # Oldest first; draft untouched.

Set-TestPages @(,$releases)
Remove-OldGitHubReleases -Owner test -Repo test -Token fake -KeepCount 2 -CurrentReleaseId 1
Assert-Ids @(2, 3, 4) # Republished old release still protected.

Set-TestPages @(,$releases)
Remove-OldGitHubReleases -Owner test -Repo test -Token fake -KeepCount 2 -Preview
Assert-Ids @() # Cleanup-only preview must never delete.

Set-TestPages @(,$releases)
Remove-OldGitHubReleases -Owner test -Repo test -Token fake -KeepCount 1
Assert-Ids @(1, 2, 3, 4) # Cleanup-only protects newest.

Set-TestPages @(,$releases)
$failed = $false
try { Remove-OldGitHubReleases -Owner test -Repo test -Token fake -KeepCount 1 -CurrentReleaseId 12345 }
catch { $failed = $true }
if (-not $failed) { throw 'Missing current release must fail before deletion.' }
Assert-Ids @()

Set-TestPages @(,@())
Remove-OldGitHubReleases -Owner test -Repo test -Token fake -KeepCount 10
Assert-Ids @()

$many = @(1..105 | ForEach-Object { New-TestRelease $_ ([DateTimeOffset]::Parse('2026-01-01T00:00:00Z').AddDays($_).ToString('o')) })
Set-TestPages @($many[0..99], $many[100..104])
Remove-OldGitHubReleases -Owner test -Repo test -Token fake -KeepCount 103 -CurrentReleaseId 105
Assert-Ids @(1, 2)
if ($script:Reads -ne 2) { throw 'Retention must fetch every page.' }

Write-Host 'PASS: 7 release retention scenarios; no network requests performed.'
