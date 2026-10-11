$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$archive = Join-Path $PSScriptRoot 'downloads\zapret-discord-youtube-1.10.3.zip'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $archive) | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri 'https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.3/zapret-discord-youtube-1.10.3.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '244314ae1c24538a0d751601da8e0c925c843371eec4456eb15f14c4fd6b7058') { throw 'Official archive SHA-256 mismatch' }
$target = Join-Path $PSScriptRoot 'downloads\official-1.10.3'
if (-not (Test-Path -LiteralPath (Join-Path $target 'zapret-discord-youtube-1.10.3\bin\winws.exe'))) { Expand-Archive -LiteralPath $archive -DestinationPath $target -Force }
Write-Output 'Official engine 1.10.3 verified'
