# Run game and keep it running for diagnostics
$gamePath = "C:\Users\Humulus.MSI\Documents\Mydata\Programming\MDiceV2\MDiceV2_Debug\MDiceV2.Launcher.exe"
$diagFile = "C:\Windows\Temp\abot_registry_diagnostic.txt"

Write-Host "Starting game..."
$proc = Start-Process -FilePath $gamePath -PassThru -NoNewWindow

if ($proc) {
    Write-Host "Game process started with PID: $($proc.Id)"
    Write-Host "Waiting 10 seconds for skill registration..."
    Start-Sleep -Seconds 10
    
    if (Test-Path $diagFile) {
        Write-Host "Diagnostic file found! Contents:"
        $content = Get-Content $diagFile
        $content | ForEach-Object { Write-Host "$_" }
    } else {
        Write-Host "ERROR: Diagnostic file not created!"
    }
    
    Write-Host "Killing game process..."
    Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
} else {
    Write-Host "ERROR: Failed to start game!"
}
