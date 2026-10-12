$ErrorActionPreference='Stop'
$ownerRoot=Split-Path -Parent $PSScriptRoot
$ownerFixture=Join-Path $ownerRoot ('tests\fixture-owner-'+[guid]::NewGuid().ToString('N'))
$ownerCompiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$ownerTestExe=Join-Path $ownerRoot 'tests\OwnerAssistantTests.exe'
& $ownerCompiler /nologo /target:exe /main:OwnerAssistantTests /platform:x64 /codepage:65001 "/out:$ownerTestExe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "$PSScriptRoot\OwnerAssistant.cs" "$PSScriptRoot\OwnerKeyStore.cs" "$ownerRoot\tests\OwnerAssistantTests.cs"
if($LASTEXITCODE -ne 0){throw 'Owner assistant test build failed'}
& $ownerTestExe $ownerRoot $ownerFixture
if($LASTEXITCODE -ne 0){throw 'Owner assistant tests failed'}
. "$PSScriptRoot\portable-key.ps1"
$ownerPassword=ConvertTo-SecureString 'Only-A-Test-Password-Not-A-Real-Secret' -AsPlainText -Force
$ownerPlain=Unprotect-PortableKey ([IO.File]::ReadAllText("$ownerFixture\csharp-backup.key-backup.json")) $ownerPassword
try {
    $ownerRsa=[Security.Cryptography.RSACryptoServiceProvider]::new();$ownerRsa.PersistKeyInCsp=$false
    try{$ownerRsa.FromXmlString([Text.Encoding]::UTF8.GetString($ownerPlain));if($ownerRsa.ToXmlString($false) -ne [IO.File]::ReadAllText("$ownerFixture\public.xml")){throw 'C# backup differs from fixture key'}}finally{$ownerRsa.Dispose()}
    [IO.File]::WriteAllText("$ownerFixture\powershell-backup.key-backup.json",(Protect-PortableKey $ownerPlain $ownerPassword))
}finally{[Array]::Clear($ownerPlain,0,$ownerPlain.Length);$ownerPassword.Dispose()}
& $ownerTestExe $ownerRoot $ownerFixture '--import-powershell'
if($LASTEXITCODE -ne 0){throw 'PowerShell and GUI backup compatibility failed'}
Write-Output 'PASS: graphical assistant and PowerShell backups are interoperable in both directions'
