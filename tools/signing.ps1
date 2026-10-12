$ErrorActionPreference = 'Stop'
function Get-ByPassSigningKey {
    param([string]$KeyPath = (Join-Path $env:LOCALAPPDATA 'ByPass\Signing\release.key'))
    if (-not (Test-Path -LiteralPath $KeyPath)) { throw 'Offline signing key is missing. Run tools/initialize-signing.ps1 on the release owner computer.' }
    $protected = [IO.File]::ReadAllBytes($KeyPath)
    $plain = [Security.Cryptography.ProtectedData]::Unprotect($protected, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    try {
        $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
        $rsa.PersistKeyInCsp = $false
        $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($plain))
        $public = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\security\update-public.xml')).Trim()
        if ($rsa.ToXmlString($false) -ne $public) { $rsa.Dispose(); throw 'The local signing key does not match the pinned public key.' }
        return $rsa
    } finally { [Array]::Clear($plain, 0, $plain.Length) }
}
function Add-ByPassSignature {
    param([string]$Json, [Security.Cryptography.RSACryptoServiceProvider]$Key, [switch]$LegacyFields)
    $bytes = [Text.Encoding]::UTF8.GetBytes($Json)
    $signature = $Key.SignData($bytes, [Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
    $envelope = if ($LegacyFields) { $Json | ConvertFrom-Json -AsHashtable } else { @{} }
    $envelope['signed_payload'] = [Convert]::ToBase64String($bytes)
    $envelope['signature'] = [Convert]::ToBase64String($signature)
    return ($envelope | ConvertTo-Json -Depth 12)
}
