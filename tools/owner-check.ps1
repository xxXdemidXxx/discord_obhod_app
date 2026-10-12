$ErrorActionPreference='Stop'
$ownerRoot=Split-Path -Parent $PSScriptRoot
if($PSVersionTable.PSVersion.Major -lt 7){throw 'PowerShell 7 is required'}
git --version
if($LASTEXITCODE -ne 0){throw 'Git is required'}
. "$PSScriptRoot\signing.ps1"
$ownerKey=Get-ByPassSigningKey
try {
    $ownerChallenge=[Text.Encoding]::UTF8.GetBytes([guid]::NewGuid().ToString())
    $ownerSignature=$ownerKey.SignData($ownerChallenge,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    if(-not $ownerKey.VerifyData($ownerChallenge,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),$ownerSignature)){throw 'Signing check failed'}
}finally{$ownerKey.Dispose()}
$ownerState=git -C $ownerRoot status --porcelain
if($LASTEXITCODE -ne 0){throw 'Not a Git checkout'}
if($ownerState){throw 'Есть незакоммиченные изменения. Сохраните нужные изменения в commit перед упаковкой официального релиза.'}
Write-Output 'Ключ проверен тестовой подписью. Все изменения сохранены в commit. Можно собирать подписанный пакет. Для публикации отдельно нужны новый номер версии, успешный GitHub Actions и свежая проверка Defender.'
