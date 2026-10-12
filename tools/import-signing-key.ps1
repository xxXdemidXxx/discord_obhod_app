param([Parameter(Mandatory)][string]$Source,[Security.SecureString]$Password,[string]$KeyPath=(Join-Path $env:LOCALAPPDATA 'ByPass\Signing\release.key'))
$ErrorActionPreference='Stop'
. "$PSScriptRoot\portable-key.ps1"
if(Test-Path -LiteralPath $KeyPath){throw 'A local signing key already exists; it will not be overwritten'}
if((Get-Item -LiteralPath $Source).Length -gt 65536){throw 'Key backup is too large'}
if(-not $Password){$Password=Read-Host 'Password for encrypted key backup' -AsSecureString}
$plain=Unprotect-PortableKey ([IO.File]::ReadAllText([IO.Path]::GetFullPath($Source))) $Password
try {
    $rsa=[Security.Cryptography.RSACryptoServiceProvider]::new();$rsa.PersistKeyInCsp=$false
    try {
        $rsa.FromXmlString([Text.Encoding]::UTF8.GetString($plain))
        if($rsa.PublicOnly -or $rsa.ToXmlString($false) -ne [IO.File]::ReadAllText((Join-Path $PSScriptRoot '..\security\update-public.xml')).Trim()){throw 'Backup does not contain the private key for this project'}
        Set-ByPassPrivateDirectory (Split-Path -Parent ([IO.Path]::GetFullPath($KeyPath)))
        $protected=[Security.Cryptography.ProtectedData]::Protect($plain,$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
        $stream=[IO.FileStream]::new([IO.Path]::GetFullPath($KeyPath),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($protected,0,$protected.Length)}finally{$stream.Dispose()}
    }finally{$rsa.Dispose()}
}finally{[Array]::Clear($plain,0,$plain.Length)}
Write-Output 'Imported the same release key for this Windows account. Public key and project identity remain unchanged.'
