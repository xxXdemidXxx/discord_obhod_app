param([Parameter(Mandatory)][string]$Path, [string]$Thumbprint=$env:BYPASS_SIGNING_THUMBPRINT)
$ErrorActionPreference='Stop'
if (-not $Thumbprint) { Write-Output 'Trusted Windows publisher certificate is not configured; executable remains unsigned.'; return }
if ($Thumbprint -notmatch '^[a-fA-F0-9]{40}$') {throw 'Invalid signing certificate thumbprint'}
$cert=Get-Item -LiteralPath "Cert:\CurrentUser\My\$Thumbprint" -ErrorAction Stop
if (-not $cert.HasPrivateKey -or $cert.NotAfter -le [DateTime]::UtcNow -or $cert.NotBefore -gt [DateTime]::UtcNow) {throw 'Signing certificate is missing its private key or is not valid now'}
if (-not ($cert.EnhancedKeyUsageList.ObjectId -contains '1.3.6.1.5.5.7.3.3')) {throw 'Certificate is not intended for code signing'}
$chain=[Security.Cryptography.X509Certificates.X509Chain]::new()
$chain.ChainPolicy.RevocationMode=[Security.Cryptography.X509Certificates.X509RevocationMode]::Online
try {if (-not $chain.Build($cert)) {throw 'Publisher certificate chain is not trusted'}} finally {$chain.Dispose()}
$signature=Set-AuthenticodeSignature -LiteralPath $Path -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
if ($signature.Status -ne 'Valid' -or -not $signature.TimeStamperCertificate) {throw 'Authenticode signing or timestamp validation failed'}
Write-Output "Signed executable: $Path"
