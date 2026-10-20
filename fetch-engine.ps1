param([ValidateSet('1.10.3','1.10.4')][string]$Version='1.10.3')
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$archive = Join-Path $PSScriptRoot "downloads\zapret-discord-youtube-$Version.zip"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $archive) | Out-Null
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://github.com/Flowseal/zapret-discord-youtube/releases/download/$Version/zapret-discord-youtube-$Version.zip" -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $(if($Version -eq '1.10.4'){'291af673bd0238506f9548e0e94ca0abf244000f4c9a06960544fb01c5f97b34'}else{'244314ae1c24538a0d751601da8e0c925c843371eec4456eb15f14c4fd6b7058'})) { throw 'Official archive SHA-256 mismatch' }
$target = Join-Path $PSScriptRoot "downloads\official-$Version"
if (-not (Test-Path -LiteralPath (Join-Path $target "zapret-discord-youtube-$Version\bin\winws.exe"))) { Expand-Archive -LiteralPath $archive -DestinationPath $target -Force }
Write-Output "Official engine $Version verified"
