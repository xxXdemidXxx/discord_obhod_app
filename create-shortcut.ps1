param([string]$DestinationDirectory = [Environment]::GetFolderPath('DesktopDirectory'))
$ErrorActionPreference = 'Stop'
$version = ([version]([regex]::Match((Get-Content -LiteralPath "$PSScriptRoot\src\Version.cs" -Raw), 'Value = "([0-9.]+)"').Groups[1].Value)).ToString(3)
$app = Join-Path $PSScriptRoot "dist\Obhod-$version.exe"
if (-not (Test-Path -LiteralPath $app)) {throw 'Build the application first'}
$shortcutPath = Join-Path $DestinationDirectory 'Обход.lnk'
if (Test-Path -LiteralPath $shortcutPath) {
    $existing = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcutPath)
    if ($existing.TargetPath -ne $app -and $existing.TargetPath -ne (Join-Path $PSScriptRoot 'dist\Obhod.exe')) {throw "A different shortcut already exists: $shortcutPath"}
}
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $app
$shortcut.WorkingDirectory = Split-Path -Parent $app
$shortcut.IconLocation = "$app,0"
$shortcut.Description = 'Открыть окно управления обходом для Discord и YouTube'
$shortcut.Save()
Write-Output "Created shortcut: $shortcutPath"

