param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
& "$project/fetch-engine.ps1" -Version 1.10.4
& "$project/build.ps1"
$fileVersion=[regex]::Match([IO.File]::ReadAllText("$project/src/Version.cs"),'Value = "([0-9.]+)"').Groups[1].Value
$version=([version]$fileVersion).ToString(3)
$destination=Join-Path $project ('releases/driver-stop-preview-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$app=Join-Path $destination 'ByPass-Driver-Stop-Test'
New-Item -ItemType Directory -Path $app | Out-Null
Copy-Item -LiteralPath "$project/dist/ByPass-$version.exe" -Destination "$app/ByPass.exe"
Copy-Item -LiteralPath "$project/dist/ByPass-$version.exe.config" -Destination "$app/ByPass.exe.config"
Copy-Item -LiteralPath "$project/LICENSE","$project/TESTING-GUIDE.txt" -Destination $app
Copy-Item -LiteralPath "$project/licenses" -Destination "$app/licenses" -Recurse
Copy-Item -LiteralPath "$project/docs/DRIVER-SHUTDOWN.md" -Destination "$app/DRIVER-SHUTDOWN.txt"
$archive="$project/downloads/zapret-discord-youtube-1.10.4.zip"
$archiveHash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if($archiveHash -ne '291af673bd0238506f9548e0e94ca0abf244000f4c9a06960544fb01c5f97b34'){throw 'Official archive SHA-256 mismatch'}
$engine=Join-Path $app 'data/engines/1.10.4'
New-Item -ItemType Directory -Path (Split-Path -Parent $engine) -Force | Out-Null
Copy-Item -LiteralPath "$project/downloads/official-1.10.4/zapret-discord-youtube-1.10.4" -Destination $engine -Recurse
Copy-Item -LiteralPath $archive -Destination "$engine/.upstream-archive.zip"
@{version='1.10.4';archive_sha256=$archiveHash;archive_size=(Get-Item -LiteralPath $archive).Length} | ConvertTo-Json -Compress | Set-Content -LiteralPath "$engine/.upstream-release.json" -Encoding utf8
foreach($flag in @('game_filter.enabled','telegram_filter.enabled','telegram_relay.enabled')){if(Test-Path -LiteralPath "$engine/utils/$flag"){Remove-Item -LiteralPath "$engine/utils/$flag"}}
'disabled' | Set-Content -LiteralPath "$engine/utils/discord_filter.disabled" -Encoding ascii
foreach($name in @('list-general-user.txt','list-exclude-user.txt','ipset-exclude-user.txt')){
    $(if($name.StartsWith('ipset')){'203.0.113.113/32'}else{'domain.example.abc'}) | Set-Content -LiteralPath "$engine/lists/$name" -Encoding ascii
}
(Get-FileHash -LiteralPath "$engine/lists/ipset-all.txt" -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content -LiteralPath "$engine/.upstream-ipset.sha256" -Encoding ascii
@{Engine='1.10.4';Strategy='general.bat';CheckUpdates=$false;AutoAppUpdates=$false;AutoInstallShell=$false;AutoInstallCore=$false;StartWithWindows=$false} | ConvertTo-Json | Set-Content -LiteralPath "$app/data/settings.json" -Encoding utf8
"ByPass $fileVersion — локальная проверка полной остановки. Не опубликовано; подпись автора отсутствует. Оба обхода, резерв Telegram, автообновления и автозапуск выключены. Распакуйте целиком в отдельную папку. Настоящая выгрузка драйвера и UAC требуют ручной проверки по DRIVER-SHUTDOWN.txt. Не запускайте одновременно со старой копией." | Set-Content -LiteralPath "$app/PREVIEW.txt" -Encoding utf8
$zip=Join-Path $destination 'ByPass-Driver-Stop-Test.zip'
Compress-Archive -LiteralPath $app -DestinationPath $zip
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  ByPass-Driver-Stop-Test.zip" | Set-Content -LiteralPath "$zip.sha256.txt" -Encoding ascii
Write-Output "Preview: $zip"
Write-Output "FileVersion: $fileVersion"
Write-Output "SHA-256: $hash"
