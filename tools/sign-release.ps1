param([string]$ReleaseDirectory=(Join-Path $PSScriptRoot '..\releases'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'signing.ps1')
$metadata=Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'update-unsigned.json') -Raw | ConvertFrom-Json -AsHashtable
if($metadata.source_dirty -or $metadata.source_commit -ne (& git -C (Join-Path $PSScriptRoot '..') rev-parse HEAD) -or (& git -C (Join-Path $PSScriptRoot '..') status --porcelain)){throw 'Build and sign from the same clean committed source tree'}
$reportPath=Join-Path $ReleaseDirectory 'security-report.json'
$report=Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if($report.result -ne 'clean' -or $report.exit_code -ne 0 -or [DateTime]::Parse($report.scanned_at).ToUniversalTime() -lt [DateTime]::UtcNow.AddDays(-1)){throw 'A successful antivirus scan from the last 24 hours is required before signing'}
foreach($entry in $report.files){$path=Join-Path $ReleaseDirectory $entry.name;if((Get-Item -LiteralPath $path).Length -ne $entry.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Release file changed after antivirus scan: $($entry.name)"}}
$asset=$metadata.assets[0];$path=Join-Path $ReleaseDirectory $asset.name
if($asset.digest -ne 'sha256:'+(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -or $asset.size -ne (Get-Item -LiteralPath $path).Length){throw 'Manifest archive hash or size does not match the build'}
# Validate the signed per-file hashes against the exact reviewed archive too.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($path)
try {
    $entry=$archive.GetEntry('Obhod/build-proof.json');if(-not $entry){throw 'Owner-signed build receipt is missing; package with -OwnerSignature'}
    $reader=[IO.StreamReader]::new($entry.Open())
    try{$receipt=$reader.ReadToEnd() | ConvertFrom-Json}finally{$reader.Dispose()}
    $public=[Security.Cryptography.RSACryptoServiceProvider]::new();$public.PersistKeyInCsp=$false
    try {
        $public.FromXmlString([IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\security\update-public.xml')))
        $bytes=[Convert]::FromBase64String($receipt.signed_payload)
        if(-not $public.VerifyData($bytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),[Convert]::FromBase64String($receipt.signature))){throw 'Owner build receipt signature is invalid'}
        $identity=[Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
        if($identity.kind -ne 'ByPass-build' -or $identity.owner -ne 'xxXdemidXxx' -or ('v'+$identity.version) -ne $metadata.tag_name -or $identity.source_commit -ne $metadata.source_commit -or $identity.shell_files.exe -ne $metadata.shell_files.exe -or $identity.shell_files.config -ne $metadata.shell_files.config){throw 'Owner build receipt does not identify this release'}
    }finally{$public.Dispose()}
    $assetProof=Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'build-proof.json') -Raw | ConvertFrom-Json
    if($assetProof.signed_payload -ne $receipt.signed_payload -or $assetProof.signature -ne $receipt.signature){throw 'Standalone build receipt differs from the archive'}
    foreach($pair in @(@('Obhod/Obhod.exe','exe'),@('Obhod/Obhod.exe.config','config'))){
        $entry=$archive.GetEntry($pair[0]);if(-not $entry){throw 'Missing shell file in release archive'}
        $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
        try{$actual=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant();if($actual -ne $metadata.shell_files[$pair[1]]){throw 'Shell hashes differ from reviewed archive'}}finally{$stream.Dispose();$sha.Dispose()}
    }
}finally{$archive.Dispose()}
$metadata.security_report_sha256=(Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
$metadata.issued_at=[DateTime]::UtcNow.ToString('o')
$key=Get-ByPassSigningKey
try{[IO.File]::WriteAllText((Join-Path $ReleaseDirectory 'update.json'),(Add-ByPassSignature -Json ($metadata | ConvertTo-Json -Depth 12 -Compress) -Key $key -LegacyFields),[Text.UTF8Encoding]::new($false))}finally{$key.Dispose()}
Write-Output 'Signed update.json with the offline release key.'
