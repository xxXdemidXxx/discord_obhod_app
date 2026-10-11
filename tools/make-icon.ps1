$ErrorActionPreference = 'Stop'
$projectDir = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$assets = Join-Path $projectDir 'assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
$generator = Join-Path $PSScriptRoot 'CreateIcon.exe'
& $compiler /nologo /target:exe "/out:$generator" /reference:System.Drawing.dll (Join-Path $PSScriptRoot 'CreateIcon.cs')
if ($LASTEXITCODE -ne 0) {throw 'Icon generator build failed'}
& $generator (Join-Path $assets 'Obhod.ico')
if ($LASTEXITCODE -ne 0) {throw 'Icon generation failed'}
