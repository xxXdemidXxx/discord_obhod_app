param(
    [string]$LocalEngine = (Join-Path $PSScriptRoot '..\zapret-discord-youtube-1.10.2'),
    [string]$OfficialEngine = (Join-Path $PSScriptRoot 'downloads\official-1.10.3\zapret-discord-youtube-1.10.3')
)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'build.ps1')
if (-not (Test-Path -LiteralPath (Join-Path $LocalEngine 'bin\winws.exe'))) { throw 'Missing local engine' }
if (-not (Test-Path -LiteralPath (Join-Path $OfficialEngine 'bin\winws.exe'))) { throw 'Missing official 1.10.3 engine' }
$archive = Join-Path $PSScriptRoot 'downloads\zapret-discord-youtube-1.10.3.zip'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '244314ae1c24538a0d751601da8e0c925c843371eec4456eb15f14c4fd6b7058') { throw 'Official archive SHA-256 mismatch' }
$dist = Join-Path $PSScriptRoot 'dist'
$engines = Join-Path $dist 'data\engines'
New-Item -ItemType Directory -Force -Path $engines | Out-Null
$previous = Join-Path $engines '1.10.2'
$current = Join-Path $engines '1.10.3'
if (-not (Test-Path -LiteralPath $previous)) { Copy-Item -LiteralPath $LocalEngine -Destination $previous -Recurse }
if (-not (Test-Path -LiteralPath $current)) {
    Copy-Item -LiteralPath $OfficialEngine -Destination $current -Recurse
    $hash = (Get-FileHash -LiteralPath (Join-Path $current 'lists\ipset-all.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $current '.upstream-ipset.sha256') -Value $hash -Encoding ASCII
    Get-ChildItem -LiteralPath (Join-Path $previous 'lists') -Filter '*-user.txt' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $current 'lists') -Force
    }
    foreach ($relative in @('lists\ipset-all.txt', 'lists\ipset-all.txt.backup', 'utils\game_filter.enabled', 'bin\ACTIVE_GAME_UDP.bin', 'bin\ACTIVE_DISCORD_UDP.bin')) {
        $source = Join-Path $previous $relative
        if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $current $relative) -Force }
    }
}
$settings = Join-Path $dist 'data\settings.json'
if (-not (Test-Path -LiteralPath $settings)) {
    @{ Engine='1.10.3'; PreviousEngine='1.10.2'; Strategy='general.bat'; CheckUpdates=$true } | ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding UTF8
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md') -Destination $dist -Force
if (-not (Test-Path -LiteralPath (Join-Path $dist 'licenses'))) { New-Item -ItemType Directory -Path (Join-Path $dist 'licenses') | Out-Null }
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'licenses') -File | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $dist 'licenses') -Force }
Write-Output "Ready: $dist (versioned Obhod EXE)"

