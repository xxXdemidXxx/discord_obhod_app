param([switch]$Network)
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'fetch-engine.ps1')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$source = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName
$output = Join-Path $PSScriptRoot 'tests\Tests.exe'
& $compiler /nologo /target:exe /main:ZapretDesktop.Tests /platform:x64 "/out:$output" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $source (Join-Path $PSScriptRoot 'tests\Tests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test build failed' }
& $compiler /nologo /target:winexe /main:ZapretDesktop.UpdateProbe /platform:x64 "/out:$PSScriptRoot\tests\Obhod-probe.exe" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $source (Join-Path $PSScriptRoot 'tests\UpdateProbe.cs')
if ($LASTEXITCODE -ne 0) { throw 'Update probe build failed' }
if ($Network) { & $output --network } else { & $output }
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }

