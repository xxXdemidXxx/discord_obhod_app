$ErrorActionPreference = 'Stop'
$projectDir = $PSScriptRoot
$version = ([version]([regex]::Match((Get-Content -LiteralPath "$projectDir\src\Version.cs" -Raw), 'Value = "([0-9.]+)"').Groups[1].Value)).ToString(3)
$exe = "Obhod-$version.exe"
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$dist = Join-Path $projectDir 'dist'
New-Item -ItemType Directory -Force -Path $dist | Out-Null
$source = Get-ChildItem -LiteralPath (Join-Path $projectDir 'src') -Filter '*.cs' | ForEach-Object FullName
$icon = Join-Path $projectDir 'assets\Obhod.ico'
if (-not (Test-Path -LiteralPath $icon)) { & (Join-Path $projectDir 'tools\make-icon.ps1') }
$trustResources = @("/resource:$projectDir\security\update-public.xml,ZapretDesktop.UpdatePublicKey", "/resource:$projectDir\security\engine-trust.json,ZapretDesktop.EngineTrust")
& $compiler /nologo /target:winexe /main:ZapretDesktop.EngineHost /optimize+ /platform:x64 "/out:$dist\ByPass.EngineHost.exe" "/win32manifest:$projectDir\app.manifest" @trustResources /reference:System.Web.Extensions.dll "$projectDir\src\EngineHost.cs" "$projectDir\src\Trust.cs" "$projectDir\src\Engine.cs" "$projectDir\src\OwnedJob.cs"
if ($LASTEXITCODE -ne 0) { throw 'Engine host build failed' }
& "$projectDir\tools\sign-executable.ps1" -Path "$dist\ByPass.EngineHost.exe"
& $compiler /nologo /target:winexe /main:ZapretDesktop.Program /optimize+ /platform:x64 "/out:$dist\$exe" "/win32manifest:$projectDir\app.manifest" "/win32icon:$icon" "/resource:$icon,ZapretDesktop.Icon" "/resource:$dist\ByPass.EngineHost.exe,ZapretDesktop.EngineHost" @trustResources /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $source
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
& "$projectDir\tools\sign-executable.ps1" -Path "$dist\$exe"
@'
<?xml version="1.0"?>
<configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup></configuration>
'@ | Set-Content -LiteralPath (Join-Path $dist "$exe.config") -Encoding UTF8
# Retain the assembly name accepted by installed Obhod updaters, while the
# user-facing executable and Windows product information use ByPass.
Copy-Item -LiteralPath (Join-Path $dist $exe) -Destination (Join-Path $dist "ByPass-$version.exe") -Force
Copy-Item -LiteralPath (Join-Path $dist "$exe.config") -Destination (Join-Path $dist "ByPass-$version.exe.config") -Force
Write-Output "Built: $dist\ByPass-$version.exe"
