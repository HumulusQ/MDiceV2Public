param(
    [string]$Message = ("chore: sync local changes " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss"))
)

$ErrorActionPreference = "Stop"
$PSNativeCommandUseErrorActionPreference = $true

Set-Location -LiteralPath $PSScriptRoot

$branch = (git rev-parse --abbrev-ref HEAD).Trim()
if (-not $branch) {
    throw "Unable to determine the current git branch."
}

$statusBeforeAdd = git status --porcelain
if (-not $statusBeforeAdd) {
    Write-Host "No local changes detected. Nothing to commit or push."
    exit 0
}

Write-Host "Staging all local changes..."
git add -A

Write-Host "Creating commit on branch '$branch'..."
git commit -m $Message

$upstreamBranch = $null
try {
    $upstreamBranch = (git rev-parse --abbrev-ref --symbolic-full-name "@{u}").Trim()
} catch {
    $upstreamBranch = $null
}

if ($upstreamBranch) {
    Write-Host "Pushing to existing upstream..."
    git push
} else {
    Write-Host "No upstream configured. Pushing and setting upstream to origin/$branch..."
    git push --set-upstream origin $branch
}

Write-Host "Done."
