param([Parameter(Mandatory)][string]$Destination,[Security.SecureString]$Password)
$ErrorActionPreference='Stop'
. "$PSScriptRoot\signing.ps1"
. "$PSScriptRoot\portable-key.ps1"
$target=[IO.Path]::GetFullPath($Destination)
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')+'\'
if($target.StartsWith($repo,[StringComparison]::OrdinalIgnoreCase)){throw 'Store the encrypted key backup outside the repository'}
if(Test-Path -LiteralPath $target){throw 'Destination already exists; key backups are never overwritten'}
if(-not $Password){$Password=Read-Host 'Password for encrypted key backup (at least 16 characters; keep it in your password manager)' -AsSecureString}
$key=Get-ByPassSigningKey
try {
    $plain=[Text.Encoding]::UTF8.GetBytes($key.ToXmlString($true))
    try {
        $json=Protect-PortableKey $plain $Password
        # Confirm the password again before creating the only portable copy.
        $confirm=Read-Host 'Enter the same password again' -AsSecureString
        $check=Unprotect-PortableKey $json $confirm
        [Array]::Clear($check,0,$check.Length)
        $bytes=[Text.Encoding]::UTF8.GetBytes($json)
        $stream=[IO.FileStream]::new($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()}
    }finally{[Array]::Clear($plain,0,$plain.Length)}
}finally{$key.Dispose()}
Write-Output "Encrypted backup saved: $target. Transfer only this encrypted file; keep its password separately."
