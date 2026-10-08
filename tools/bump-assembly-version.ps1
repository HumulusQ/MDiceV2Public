[CmdletBinding()]
param(
    [string]$PropsPath = "../Version.props"
)

$ErrorActionPreference = "Stop"

# Resolve path relative to script
$propsFull = Resolve-Path -Path (Join-Path $PSScriptRoot $PropsPath)
[string]$cachePath = Join-Path $PSScriptRoot ".assemblyversion.base"
[xml]$xml = Get-Content -Path $propsFull

$pg = $xml.Project.PropertyGroup | Select-Object -First 1
if (-not $pg) { throw "PropertyGroup not found in $propsFull" }

$curNode = $pg.AssemblyVersion
if (-not $curNode) { throw "AssemblyVersion not found in $propsFull" }
$cur = $curNode.InnerText

$parts = ($cur -split '\.') | ForEach-Object { [int]$_ }
while ($parts.Count -lt 4) { $parts += 0 }

# 记录当前基线（前三段）
$currentBase = ($parts[0..2] -join '.')

# 从缓存读取上次基线，若变动则将修订号清零
$lastBase = $null
if (Test-Path $cachePath) {
    $lastBase = (Get-Content -Raw -Path $cachePath).Trim()
}

if ([string]::IsNullOrWhiteSpace($lastBase) -or $lastBase -ne $currentBase) {
    $parts[3] = 0
}

$parts[3] = $parts[3] + 1
$newVersion = ($parts -join '.')

# 更新缓存中的基线（前三段）
Set-Content -Path $cachePath -Value $currentBase -Encoding UTF8

$pg.AssemblyVersion = $newVersion
if ($pg.FileVersion) { $pg.FileVersion = $newVersion } else { $pg.AppendChild($xml.CreateElement('FileVersion')).InnerText = $newVersion }
if ($pg.InformationalVersion) { $pg.InformationalVersion = $newVersion } else { $pg.AppendChild($xml.CreateElement('InformationalVersion')).InnerText = $newVersion }

$xml.Save($propsFull)
Write-Host "AssemblyVersion bumped to $newVersion in $propsFull"
