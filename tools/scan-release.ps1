param([string]$ReleaseDirectory=(Join-Path $PSScriptRoot '..\releases'))
$ErrorActionPreference='Stop'
$metadata=Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'update-unsigned.json') -Raw | ConvertFrom-Json
$compat=Join-Path $ReleaseDirectory $metadata.assets[0].name
$brand=Join-Path $ReleaseDirectory 'ByPass-win-x64.zip'
$status=Get-MpComputerStatus -ErrorAction Stop
if(-not $status.AMServiceEnabled -or -not $status.AntivirusEnabled){throw 'Microsoft Defender Antivirus is unavailable; release scan cannot pass'}
if($status.AntivirusSignatureLastUpdated -lt [DateTime]::Now.AddDays(-2)){throw 'Defender definitions are older than 48 hours; update definitions before scanning'}
$scanner=Get-ChildItem -LiteralPath (Join-Path $env:ProgramData 'Microsoft\Windows Defender\Platform') -Directory | Sort-Object Name -Descending | ForEach-Object {Join-Path $_.FullName 'MpCmdRun.exe'} | Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
if(-not $scanner){$scanner=Join-Path $env:ProgramFiles 'Windows Defender\MpCmdRun.exe'}
if(-not(Test-Path -LiteralPath $scanner)){throw 'Microsoft Defender command-line scanner was not found'}
$stage=Join-Path $ReleaseDirectory ('scan-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
try {
    Copy-Item -LiteralPath $brand,$compat -Destination $stage
    Expand-Archive -LiteralPath $brand -DestinationPath (Join-Path $stage 'unpacked')
    $scanOutput= & $scanner -Scan -ScanType 3 -File $stage -DisableRemediation 2>&1
    $code=$LASTEXITCODE
    $scanOutput | Out-String | Set-Content -LiteralPath (Join-Path $ReleaseDirectory 'defender-scan.log') -Encoding utf8
    if($code -ne 0){throw "Defender scan failed or detected a threat (exit $code). See releases/defender-scan.log. Do not publish this build."}
    $files=@(Get-Item -LiteralPath $brand,$compat | ForEach-Object {@{name=$_.Name;size=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
    $signature=Get-AuthenticodeSignature -LiteralPath (Join-Path $stage 'unpacked\ByPass\ByPass.exe')
    @{schema=1;result='clean';scanner='Microsoft Defender Antivirus';scanned_at=[DateTime]::UtcNow.ToString('o');definitions=$status.AntivirusSignatureVersion;definition_time=$status.AntivirusSignatureLastUpdated.ToUniversalTime().ToString('o');engine=$status.AMEngineVersion;exit_code=$code;publisher_signature=$signature.Status.ToString();files=$files} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $ReleaseDirectory 'security-report.json') -Encoding utf8
    Write-Output "Defender scan passed; definitions $($status.AntivirusSignatureVersion)."
}finally {
    $resolved=[IO.Path]::GetFullPath($stage);$prefix=[IO.Path]::GetFullPath($ReleaseDirectory).TrimEnd('\')+'\'
    if(-not $resolved.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid scan staging path'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
