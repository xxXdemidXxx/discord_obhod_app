param([string]$Engine = (Join-Path $PSScriptRoot '..\downloads\official-1.10.3\zapret-discord-youtube-1.10.3'), [string]$Archive = (Join-Path $PSScriptRoot '..\downloads\zapret-discord-youtube-1.10.3.zip'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'signing.ps1')
$archiveHash = (Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($archiveHash -ne '244314ae1c24538a0d751601da8e0c925c843371eec4456eb15f14c4fd6b7058') { throw 'Only the reviewed, pinned official 1.10.3 archive is approved by this script.' }
$root = [IO.Path]::GetFullPath($Engine).TrimEnd('\') + '\'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($Archive)
try {
$files = @(Get-ChildItem -LiteralPath $Engine -File -Recurse | Sort-Object FullName | ForEach-Object {
    $relative = $_.FullName.Substring($root.Length).Replace('\','/')
    if ($relative -notmatch '^lists/' -and $relative -ne 'utils/game_filter.enabled' -and $relative -notmatch '^bin/ACTIVE_(GAME|DISCORD)_UDP\.bin$' -and $relative -notmatch '^\.upstream-') {
        if($_.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse points cannot be approved'}
        $entry=$zip.GetEntry('zapret-discord-youtube-1.10.3/'+$relative)
        if(-not $entry -or $entry.Length -ne $_.Length){throw "Extracted file differs from pinned archive: $relative"}
        $stream=$entry.Open();$sha=[Security.Cryptography.SHA256]::Create()
        try{$expected=([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-','').ToLowerInvariant()}finally{$stream.Dispose();$sha.Dispose()}
        if($expected -ne (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()){throw "Extracted file differs from pinned archive: $relative"}
        @{ path=$relative; size=$_.Length; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
})
}finally{$zip.Dispose()}
$json = @{ schema=1; engines=@(@{ version='1.10.3'; archive_sha256=$archiveHash; files=$files }) } | ConvertTo-Json -Depth 8 -Compress
$key = Get-ByPassSigningKey
try { [IO.File]::WriteAllText((Join-Path $PSScriptRoot '..\security\engine-trust.json'), (Add-ByPassSignature -Json $json -Key $key), [Text.UTF8Encoding]::new($false)) }
finally { $key.Dispose() }
Write-Output "Signed the reviewed engine catalogue ($($files.Count) immutable files)."
