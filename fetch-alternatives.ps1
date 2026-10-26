param()
$ErrorActionPreference='Stop'
$cache=Join-Path $PSScriptRoot 'downloads/alternatives'
$zip=Join-Path $cache 'byedpi-17.3-x86_64-w64.zip'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if(-not (Test-Path -LiteralPath $zip)){Invoke-WebRequest -Uri 'https://github.com/hufrea/byedpi/releases/download/v0.17.3/byedpi-17.3-x86_64-w64.zip' -OutFile $zip -TimeoutSec 45}
if((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne '70d2c94147193cb915f9c6eb5144b8d404dacbcfa90bda2383b6b211afafa456'){throw 'Official ByeDPI archive SHA-256 mismatch'}
$destination=Join-Path $cache 'byedpi-0.17.3'
if(-not (Test-Path -LiteralPath (Join-Path $destination 'ciadpi.exe'))){Expand-Archive -LiteralPath $zip -DestinationPath $destination}
$image=Join-Path $destination 'ciadpi.exe'
if((Get-FileHash -LiteralPath $image -Algorithm SHA256).Hash -ne 'eb53ceeeb981cc6735ac24bb1e51e725280b86630e80fdf19ddc4ee4a5b54ef4'){throw 'Official ByeDPI EXE SHA-256 mismatch'}
Write-Output "Official ByeDPI 0.17.3 verified: $image"
