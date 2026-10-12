$ErrorActionPreference='Stop'
. "$PSScriptRoot\..\tools\portable-key.ps1"
$password=ConvertTo-SecureString ([Guid]::NewGuid().ToString('N')) -AsPlainText -Force
$plain=[Text.Encoding]::UTF8.GetBytes('Temporary test fixture; not an actual signing key')
$json=Protect-PortableKey $plain $password
$decoded=Unprotect-PortableKey $json $password
if([Convert]::ToBase64String($decoded) -ne [Convert]::ToBase64String($plain)){throw 'Encrypted backup round trip failed'}
$wrong=ConvertTo-SecureString ([Guid]::NewGuid().ToString('N')) -AsPlainText -Force
$rejected=$false;try{Unprotect-PortableKey $json $wrong | Out-Null}catch{$rejected=$true}
if(-not $rejected){throw 'Wrong backup password was accepted'}
$data=$json | ConvertFrom-Json -AsHashtable
$cipher=[Convert]::FromBase64String($data.ciphertext);$cipher[0]=$cipher[0] -bxor 1;$data.ciphertext=[Convert]::ToBase64String($cipher)
$rejected=$false;try{Unprotect-PortableKey ($data | ConvertTo-Json) $password | Out-Null}catch{$rejected=$true}
if(-not $rejected){throw 'Modified encrypted backup was accepted'}
$data=$json | ConvertFrom-Json -AsHashtable;$data.iterations=2147483647
$rejected=$false;try{Unprotect-PortableKey ($data | ConvertTo-Json) $password | Out-Null}catch{$rejected=$true}
if(-not $rejected){throw 'Unbounded backup KDF work was accepted'}
[Array]::Clear($plain,0,$plain.Length);[Array]::Clear($decoded,0,$decoded.Length)
Write-Output 'PASS: 4 portable key checks'
