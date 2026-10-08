# 测试数据加载逻辑

$launcherDir = "c:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\MDiceV2.Launcher"
$dataJsonPath = Join-Path $launcherDir "data\mods\CustomizedReply\data.json"

Write-Host "=== CustomizedReply Data Loading Test ===" -ForegroundColor Cyan

Write-Host "`n1. Checking if data.json exists:" -ForegroundColor Yellow
if (Test-Path $dataJsonPath) {
    Write-Host "✓ Found: $dataJsonPath" -ForegroundColor Green
    $fileSize = (Get-Item $dataJsonPath).Length
    Write-Host "  File size: $fileSize bytes"
} else {
    Write-Host "✗ NOT found: $dataJsonPath" -ForegroundColor Red
}

Write-Host "`n2. Checking directory structure:" -ForegroundColor Yellow
$paths = @(
    (Join-Path $launcherDir "data"),
    (Join-Path $launcherDir "data\mods"),
    (Join-Path $launcherDir "data\mods\CustomizedReply"),
    (Join-Path $launcherDir "mods"),
    (Join-Path $launcherDir "mods\CustomizedReply")
)

foreach ($path in $paths) {
    $exists = Test-Path $path
    $status = if ($exists) { "✓ EXISTS" } else { "✗ MISSING" }
    $color = if ($exists) { "Green" } else { "Red" }
    Write-Host "  $status : $path" -ForegroundColor $color
}

Write-Host "`n3. Listing files in data/mods/CustomizedReply:" -ForegroundColor Yellow
$customizedReplyPath = Join-Path $launcherDir "data\mods\CustomizedReply"
if (Test-Path $customizedReplyPath) {
    Get-ChildItem $customizedReplyPath | ForEach-Object {
        Write-Host "  - $($_.Name) ($($_.Length) bytes)" -ForegroundColor Green
    }
}

Write-Host "`n4. Checking JSON content:" -ForegroundColor Yellow
if (Test-Path $dataJsonPath) {
    $json = Get-Content $dataJsonPath | ConvertFrom-Json
    Write-Host "  Description: $($json.description)" -ForegroundColor Green
    Write-Host "  Number of rules: $($json.replies.Count)" -ForegroundColor Green
    Write-Host "  Rules:" -ForegroundColor Green
    foreach ($rule in $json.replies) {
        Write-Host "    - Trigger: '$($rule.trigger)', Type: $($rule.matchType), Replies: $($rule.replies.Count)" -ForegroundColor White
    }
}

Write-Host "`n=== Test Complete ===" -ForegroundColor Cyan
