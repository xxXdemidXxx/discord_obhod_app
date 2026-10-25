param([switch]$OwnerSignature)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'fetch-engine.ps1') -Version 1.10.4
$version = ([version]([regex]::Match((Get-Content -LiteralPath "$PSScriptRoot\src\Version.cs" -Raw), 'Value = "([0-9.]+)"').Groups[1].Value)).ToString(3)
& (Join-Path $PSScriptRoot 'build.ps1')
$archive = Join-Path $PSScriptRoot 'downloads\zapret-discord-youtube-1.10.4.zip'
if (-not (Test-Path -LiteralPath $archive)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $archive) | Out-Null
    Invoke-WebRequest -Uri 'https://github.com/Flowseal/zapret-discord-youtube/releases/download/1.10.4/zapret-discord-youtube-1.10.4.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne '291af673bd0238506f9548e0e94ca0abf244000f4c9a06960544fb01c5f97b34') {throw 'Official archive SHA-256 mismatch'}
$releaseDir = Join-Path $PSScriptRoot 'releases'
$stage = Join-Path $releaseDir ('stage-' + [Guid]::NewGuid().ToString('N'))
$appDir = Join-Path $stage 'Obhod'
New-Item -ItemType Directory -Force -Path $appDir | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "dist\Obhod-$version.exe") -Destination (Join-Path $appDir 'Obhod.exe')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "dist\Obhod-$version.exe.config") -Destination (Join-Path $appDir 'Obhod.exe.config')
if($OwnerSignature) {
    if(& git -C $PSScriptRoot status --porcelain){throw 'Owner signature requires a clean committed source tree'}
    . "$PSScriptRoot\tools\signing.ps1"
    $buildProof=@{schema=1;kind='ByPass-build';owner='xxXdemidXxx';version=$version;source_commit=(& git -C $PSScriptRoot rev-parse HEAD);shell_files=@{exe=(Get-FileHash -LiteralPath (Join-Path $appDir 'Obhod.exe') -Algorithm SHA256).Hash.ToLowerInvariant();config=(Get-FileHash -LiteralPath (Join-Path $appDir 'Obhod.exe.config') -Algorithm SHA256).Hash.ToLowerInvariant()}} | ConvertTo-Json -Depth 8 -Compress
    $key=Get-ByPassSigningKey
    try{[IO.File]::WriteAllText((Join-Path $appDir 'build-proof.json'),(Add-ByPassSignature -Json $buildProof -Key $key),[Text.UTF8Encoding]::new($false))}finally{$key.Dispose()}
    Copy-Item -LiteralPath (Join-Path $appDir 'build-proof.json') -Destination (Join-Path $releaseDir 'build-proof.json') -Force
}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination $appDir
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses') -Destination (Join-Path $appDir 'licenses') -Recurse
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'TESTING-GUIDE.txt') -Destination (Join-Path $appDir 'GUIDE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\DRIVER-SHUTDOWN.md') -Destination (Join-Path $appDir 'DRIVER-SHUTDOWN.txt')
$unpacked = Join-Path $stage 'official'
Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
$engine = Join-Path $appDir 'data\engines\1.10.4'
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $engine) | Out-Null
Copy-Item -LiteralPath (Join-Path $unpacked 'zapret-discord-youtube-1.10.4') -Destination $engine -Recurse
Copy-Item -LiteralPath $archive -Destination (Join-Path $engine '.upstream-archive.zip')
@{version='1.10.4';archive_sha256='291af673bd0238506f9548e0e94ca0abf244000f4c9a06960544fb01c5f97b34';archive_size=(Get-Item -LiteralPath $archive).Length} | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $engine '.upstream-release.json') -Encoding UTF8
$gameFilter = Join-Path $engine 'utils\game_filter.enabled'
if (Test-Path -LiteralPath $gameFilter) {Remove-Item -LiteralPath $gameFilter}
foreach ($file in @('list-general-user.txt','list-exclude-user.txt','ipset-exclude-user.txt')) {
    $value = if ($file.StartsWith('ipset')) {'203.0.113.113/32'} else {'domain.example.abc'}
    Set-Content -LiteralPath (Join-Path $engine "lists\$file") -Value $value -Encoding ASCII
}
$ipsetHash = (Get-FileHash -LiteralPath (Join-Path $engine 'lists\ipset-all.txt') -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $engine '.upstream-ipset.sha256') -Value $ipsetHash -Encoding ASCII
@{Engine='1.10.4';PreviousEngine='';Strategy='general.bat';CheckUpdates=$true;AutoAppUpdates=$true} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $appDir 'data\settings.json') -Encoding UTF8
$zip = Join-Path $releaseDir "Obhod-$version-win-x64.zip"
Compress-Archive -LiteralPath $appDir -DestinationPath $zip -Force
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($zip + '.sha256.txt') -Value "$hash  Obhod-$version-win-x64.zip" -Encoding ASCII
# Old updaters require both the Obhod asset name and Obhod/Obhod.exe layout.
# New users receive the same runtime with ByPass folder and executable names.
$brandDir = Join-Path $stage 'ByPass'
Copy-Item -LiteralPath $appDir -Destination $brandDir -Recurse
Rename-Item -LiteralPath (Join-Path $brandDir 'Obhod.exe') -NewName 'ByPass.exe'
Rename-Item -LiteralPath (Join-Path $brandDir 'Obhod.exe.config') -NewName 'ByPass.exe.config'
$download = Join-Path $releaseDir 'ByPass-win-x64.zip'
Compress-Archive -LiteralPath $brandDir -DestinationPath $download -Force
$downloadHash = (Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($download + '.sha256.txt') -Value "$downloadHash  ByPass-win-x64.zip" -Encoding ASCII
Write-Output "Download: $download"
Write-Output "Compatible update archive: $zip"
Write-Output "SHA-256: $hash"
$metadata = @{
    schema=1
    source_commit=(& git -C $PSScriptRoot rev-parse HEAD)
    source_dirty=([bool](& git -C $PSScriptRoot status --porcelain))
    shell_files=@{exe=(Get-FileHash -LiteralPath (Join-Path $appDir 'Obhod.exe') -Algorithm SHA256).Hash.ToLowerInvariant();config=(Get-FileHash -LiteralPath (Join-Path $appDir 'Obhod.exe.config') -Algorithm SHA256).Hash.ToLowerInvariant()}
    tag_name="v$version";draft=$false;prerelease=$false
    assets=@(@{name="Obhod-$version-win-x64.zip";browser_download_url="https://github.com/xxXdemidXxx/discord_obhod_app/releases/download/v$version/Obhod-$version-win-x64.zip";size=(Get-Item -LiteralPath $zip).Length;digest="sha256:$hash"})
} | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $releaseDir 'update-unsigned.json'),$metadata,(New-Object Text.UTF8Encoding($false)))
# Do not leave an older signed manifest beside a newly built archive.
if(Test-Path -LiteralPath (Join-Path $releaseDir 'update.json')){Remove-Item -LiteralPath (Join-Path $releaseDir 'update.json')}
$resolvedStage=[IO.Path]::GetFullPath($stage)
if(-not $resolvedStage.StartsWith([IO.Path]::GetFullPath($releaseDir)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid package staging path'}
Remove-Item -LiteralPath $resolvedStage -Recurse -Force
