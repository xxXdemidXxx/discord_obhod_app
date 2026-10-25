param([switch]$Network,[switch]$CoreUpdatesOnly,[switch]$TelegramOnly,[switch]$ShutdownOnly)
$ErrorActionPreference = 'Stop'
if(-not $CoreUpdatesOnly -and -not $TelegramOnly -and -not $ShutdownOnly){
    & "$PSScriptRoot\tests\PortableKeyTests.ps1"
    & "$PSScriptRoot\tests\ReleaseFlowTests.ps1"
    & "$PSScriptRoot\tools\test-owner-assistant.ps1"
}
& (Join-Path $PSScriptRoot 'fetch-engine.ps1')
& (Join-Path $PSScriptRoot 'fetch-engine.ps1') -Version 1.10.4
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName
$fixture=Join-Path $PSScriptRoot 'tests\fixture-signing'
New-Item -ItemType Directory -Force -Path $fixture | Out-Null
. "$PSScriptRoot\tools\signing.ps1"
$rsa=[Security.Cryptography.RSACryptoServiceProvider]::new(2048);$rsa.PersistKeyInCsp=$false
try {
    [IO.File]::WriteAllText("$fixture\key.private.xml",$rsa.ToXmlString($true))
    [IO.File]::WriteAllText("$fixture\public.xml",$rsa.ToXmlString($false))
    $catalogue=Get-Content -LiteralPath "$PSScriptRoot\security\engine-trust.json" -Raw | ConvertFrom-Json
    $bytes=[Convert]::FromBase64String($catalogue.signed_payload)
    $public=[Security.Cryptography.RSACryptoServiceProvider]::new();$public.PersistKeyInCsp=$false
    try {
        $public.FromXmlString([IO.File]::ReadAllText("$PSScriptRoot\security\update-public.xml"))
        if (-not $public.VerifyData($bytes,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),[Convert]::FromBase64String($catalogue.signature))) {throw 'Production engine catalogue signature is invalid'}
    } finally {$public.Dispose()}
    [IO.File]::WriteAllText("$fixture\catalogue.json",(Add-ByPassSignature -Json ([Text.Encoding]::UTF8.GetString($bytes)) -Key $rsa))
} finally {$rsa.Dispose()}
$resources=@("/resource:$fixture\public.xml,ZapretDesktop.UpdatePublicKey", "/resource:$fixture\catalogue.json,ZapretDesktop.EngineTrust", "/resource:$PSScriptRoot\assets\ByPass.ico,ZapretDesktop.Icon")
$output = Join-Path $PSScriptRoot 'tests\Tests.exe'
& $compiler /nologo /define:TESTING /target:exe /main:ZapretDesktop.Tests /platform:x64 @resources "/win32manifest:$PSScriptRoot\app.manifest" "/out:$output" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.ServiceProcess.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $source (Join-Path $PSScriptRoot 'tests\Tests.cs') (Join-Path $PSScriptRoot 'tests\TelegramMediaTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
& $compiler /nologo /define:TESTING /target:winexe /main:ZapretDesktop.UpdateProbe /platform:x64 @resources "/out:$PSScriptRoot\tests\Obhod-probe.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.ServiceProcess.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $source (Join-Path $PSScriptRoot 'tests\UpdateProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Update probe build failed' }
$legacy=@('tests\legacy-0.4\ShellUpdates.cs','src\Updates.cs','src\Engine.cs','src\Trust.cs','src\UpstreamTrust.cs','src\OwnerIdentity.cs','src\OwnedJob.cs','src\GitHubMetadata.cs','tests\LegacyBridge.cs') | ForEach-Object {Join-Path $PSScriptRoot $_}
& $compiler /nologo /target:exe /main:LegacyBridge /platform:x64 @resources "/out:$PSScriptRoot\tests\LegacyBridge.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.ServiceProcess.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $legacy
if ($LASTEXITCODE -ne 0) {throw 'Legacy updater bridge build failed'}
if($ShutdownOnly){& $output --shutdown-only} elseif($TelegramOnly){& $output --telegram-only} elseif($CoreUpdatesOnly){& $output --core-updates-only} elseif ($Network) { & $output --network } else { & $output }
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
