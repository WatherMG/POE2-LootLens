$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$targetDirectory = Join-Path $repositoryRoot "src\POE2LootLens\tessdata"
$targetFile = Join-Path $targetDirectory "rus.traineddata"
$downloadUrl = "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/main/rus.traineddata"

New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null

Write-Host "Downloading Russian Tesseract language data..."
Invoke-WebRequest -Uri $downloadUrl -OutFile $targetFile

$file = Get-Item $targetFile
if ($file.Length -lt 1MB) {
    Remove-Item $targetFile -Force -ErrorAction SilentlyContinue
    throw "Downloaded rus.traineddata is unexpectedly small: $($file.Length) bytes."
}

Write-Host "Saved: $targetFile"
Write-Host "Size:  $([Math]::Round($file.Length / 1MB, 2)) MB"
