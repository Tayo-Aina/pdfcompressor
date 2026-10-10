# Downloads the ghostscript engine dll into PdfCompressor\Assets so the project can build.
# Usage:  powershell -ExecutionPolicy Bypass -File scripts\download-engine.ps1

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$assetDir = Join-Path $root 'PdfCompressor\Assets'
$target = Join-Path $assetDir 'gsdll64.dll'

if (Test-Path $target) {
    Write-Host 'gsdll64.dll already present, nothing to do.'
    exit 0
}

New-Item -ItemType Directory -Force -Path $assetDir | Out-Null

$installer = Join-Path $env:TEMP 'gs-installer.exe'
Write-Host 'Downloading the ghostscript installer (~60 MB)...'
Invoke-WebRequest -Uri 'https://github.com/ArtifexSoftware/ghostpdl-downloads/releases/download/gs10071/gs10071w64.exe' -OutFile $installer

$sevenZip = 'C:\Program Files\7-Zip\7z.exe'
if (-not (Test-Path $sevenZip)) {
    throw '7-Zip not found. Install 7-Zip, or extract gsdll64.dll manually into PdfCompressor\Assets.'
}

& $sevenZip e $installer 'bin\gsdll64.dll' -o"$assetDir" -y | Out-Null
Write-Host "Done. Engine ready at $target"
