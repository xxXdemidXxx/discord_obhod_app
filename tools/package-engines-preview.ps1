param()
$ErrorActionPreference='Stop'
$project=Split-Path -Parent $PSScriptRoot
& "$project/fetch-alternatives.ps1"
& "$project/tools/package-driver-stop-preview.ps1"
$source=Get-ChildItem -LiteralPath "$project/releases" -Directory -Filter 'driver-stop-preview-*' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$destination=Join-Path $project ('releases/engines-preview-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
$app=Join-Path $destination 'ByPass-Engines-Test'
New-Item -ItemType Directory -Path $destination | Out-Null
Copy-Item -LiteralPath (Join-Path $source.FullName 'ByPass-Driver-Stop-Test') -Destination $app -Recurse
$alternative=Join-Path $app 'data/alternatives/byedpi-0.17.3'
New-Item -ItemType Directory -Path $alternative -Force | Out-Null
Copy-Item -LiteralPath "$project/downloads/alternatives/byedpi-0.17.3/ciadpi.exe" -Destination $alternative
Copy-Item -LiteralPath "$project/docs/ENGINES.md" -Destination "$app/ENGINES.txt"
$settings=Get-Content -LiteralPath "$app/data/settings.json" -Raw | ConvertFrom-Json
foreach($pair in @{Backend='byedpi';ProxyStrategy='split';ProxyScopeYouTube=$true;ProxyScopeDiscord=$true;StrictScope=$true;ScopeYouTube=$true;ScopeDiscord=$true;ScopeVoice=$false}.GetEnumerator()){$settings | Add-Member -NotePropertyName $pair.Key -NotePropertyValue $pair.Value -Force}
$settings | ConvertTo-Json | Set-Content -LiteralPath "$app/data/settings.json" -Encoding utf8
@{Strict=$true;YouTube=$true;Discord=$true;Voice=$false} | ConvertTo-Json | Set-Content -LiteralPath "$app/data/engines/1.10.4/utils/service-scope.json" -Encoding utf8
$previewVersion=[Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $app 'ByPass.exe')).FileVersion
"ByPass $previewVersion — локальная проверка двух движков, Telegram отдельно, выбор версии оболочки. Не опубликовано, подпись автора отсутствует. По умолчанию выбран ByeDPI, все обходы и автообновления выключены. Не запускайте одновременно с прежней копией. Инструкция: ENGINES.txt. Пакет не проходил обязательную релизную проверку Defender." | Set-Content -LiteralPath "$app/PREVIEW.txt" -Encoding utf8
$zip=Join-Path $destination 'ByPass-Engines-Test.zip'
Compress-Archive -LiteralPath $app -DestinationPath $zip
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  ByPass-Engines-Test.zip" | Set-Content -LiteralPath "$zip.sha256.txt" -Encoding ascii
Write-Output "Preview: $zip"
Write-Output "SHA-256: $hash"
