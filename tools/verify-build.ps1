param([Parameter(Mandatory)][string]$Executable,[string]$Proof)
$ErrorActionPreference='Stop'
$expectedFingerprint='813c22d95ae9b8e82d7776a5c8c87acb47c05bf3d0cfa6137ceeeb915b4e125f'
$exe=[IO.Path]::GetFullPath($Executable)
if(-not $Proof){$Proof=Join-Path (Split-Path -Parent $exe) 'build-proof.json'}
if((Get-Item -LiteralPath $Proof).Length -gt 1024*1024){throw 'Build receipt is too large'}
$public=[Security.Cryptography.RSACryptoServiceProvider]::new();$public.PersistKeyInCsp=$false
try {
    $public.FromXmlString([IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\security\update-public.xml')))
    $sha=[Security.Cryptography.SHA256]::Create()
    try{$fingerprint=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($public.ToXmlString($false))))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
    if($fingerprint -ne $expectedFingerprint){throw 'Verification key differs from the official ByPass key'}
    $receipt=Get-Content -LiteralPath $Proof -Raw | ConvertFrom-Json
    $bytes=[Convert]::FromBase64String($receipt.signed_payload)
    if(-not $public.VerifyData($bytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),[Convert]::FromBase64String($receipt.signature))){throw 'Build signature is invalid'}
    $signed=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    if($signed.schema -ne 1 -or $signed.kind -ne 'ByPass-build' -or $signed.owner -ne 'xxXdemidXxx'){throw 'Build does not identify the official ByPass author'}
    if((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant() -ne $signed.shell_files.exe -or (Get-FileHash -LiteralPath ($exe+'.config') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $signed.shell_files.config){throw 'Executable or configuration differs from the signed build'}
    if([Reflection.AssemblyName]::GetAssemblyName($exe).Version.ToString(3) -ne $signed.version){throw 'Executable version differs from signed build'}
    Write-Output "Verified ByPass $($signed.version); author: $($signed.owner); source commit: $($signed.source_commit)."
    Write-Output "Public key SHA-256: $fingerprint"
}finally{$public.Dispose()}
