param([string]$KeyPath = (Join-Path $env:LOCALAPPDATA 'ByPass\Signing\release.key'))
$ErrorActionPreference = 'Stop'
$publicPath = Join-Path $PSScriptRoot '..\security\update-public.xml'
if (Test-Path -LiteralPath $KeyPath) { throw 'A signing key already exists. It will not be overwritten.' }
if (Test-Path -LiteralPath $publicPath) { throw 'A trusted public key already exists. Key rotation must be explicit.' }
$folder = Split-Path -Parent $KeyPath
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$sid = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = New-Object Security.AccessControl.DirectorySecurity
$acl.SetAccessRuleProtection($true, $false)
$acl.SetOwner($sid)
foreach ($identity in @($sid, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
Set-Acl -LiteralPath $folder -AclObject $acl
$rsa = [Security.Cryptography.RSACryptoServiceProvider]::new(3072)
$rsa.PersistKeyInCsp = $false
try {
    $plain = [Text.Encoding]::UTF8.GetBytes($rsa.ToXmlString($true))
    try { $protected = [Security.Cryptography.ProtectedData]::Protect($plain, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser) }
    finally { [Array]::Clear($plain, 0, $plain.Length) }
    [IO.File]::WriteAllBytes($KeyPath, $protected)
    New-Item -ItemType Directory -Path (Split-Path -Parent $publicPath) -Force | Out-Null
    [IO.File]::WriteAllText($publicPath, $rsa.ToXmlString($false), [Text.UTF8Encoding]::new($false))
    Write-Output 'Created a DPAPI-protected local signing key and its public verification key. Private key contents were not printed or copied into the repository.'
} finally { $rsa.Dispose() }
