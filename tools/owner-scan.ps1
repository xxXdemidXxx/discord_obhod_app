$ErrorActionPreference='Stop'
$ownerRoot=Split-Path -Parent $PSScriptRoot
$ownerLog=Join-Path $ownerRoot 'releases\owner-scan.log'
try {
    New-Item -ItemType Directory -Path (Split-Path -Parent $ownerLog) -Force | Out-Null
    & "$PSScriptRoot\scan-release.ps1" *> $ownerLog
    exit 0
}catch {
    $_ | Out-String | Add-Content -LiteralPath $ownerLog -Encoding utf8
    exit 1
}
