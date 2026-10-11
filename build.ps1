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
& $compiler /nologo /target:winexe /optimize+ /platform:x64 "/out:$dist\$exe" "/win32manifest:$projectDir\app.manifest" "/win32icon:$icon" "/resource:$icon,ZapretDesktop.Icon" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $source
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
@'
<?xml version="1.0"?>
<configuration><startup><supportedRuntime version="v4.0" sku=".NETFramework,Version=v4.8" /></startup></configuration>
'@ | Set-Content -LiteralPath (Join-Path $dist "$exe.config") -Encoding UTF8
Write-Output "Built: $dist\$exe"
