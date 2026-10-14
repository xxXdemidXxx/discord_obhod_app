param([string]$ReleaseDirectory=(Join-Path $PSScriptRoot '..\releases'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'release-flow.ps1')
$repository='xxXdemidXxx/discord_obhod_app'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$commit= & git -C $root rev-parse HEAD
if($LASTEXITCODE -ne 0 -or (& git -C $root status --porcelain)){throw 'Publishing requires a clean committed source tree'}
$manifestPath=Join-Path $ReleaseDirectory 'update.json'
$manifest=Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$payload=[Convert]::FromBase64String($manifest.signed_payload)
$public=[Security.Cryptography.RSACryptoServiceProvider]::new();$public.PersistKeyInCsp=$false
try {
    $public.FromXmlString([IO.File]::ReadAllText((Join-Path $root 'security\update-public.xml')))
    if(-not $public.VerifyData($payload,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),[Convert]::FromBase64String($manifest.signature))){throw 'Update manifest signature is invalid'}
} finally {$public.Dispose()}
$signed=[Text.Encoding]::UTF8.GetString($payload) | ConvertFrom-Json
if($signed.source_commit -ne $commit -or $signed.source_dirty){throw 'Signed release belongs to a different source state'}
$report=Get-Content -LiteralPath (Join-Path $ReleaseDirectory 'security-report.json') -Raw | ConvertFrom-Json
if($report.result -ne 'clean' -or (ConvertTo-ByPassUtcTime $report.scanned_at) -lt [DateTime]::UtcNow.AddDays(-1)){throw 'Release scan is missing or stale'}
if((Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory 'security-report.json') -Algorithm SHA256).Hash.ToLowerInvariant() -ne $signed.security_report_sha256){throw 'Scan report changed after signing'}
foreach($entry in $report.files){if((Get-FileHash -LiteralPath (Join-Path $ReleaseDirectory $entry.name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw 'Package changed after signing'}}
# Read the existing Git credential into memory only; never print or store it.
$credential=('protocol=https'+"`n"+'host=github.com'+"`n`n" | & git -C $root credential fill)
$passwordLine=$credential | Where-Object { $_.StartsWith('password=') } | Select-Object -First 1
if(-not $passwordLine){throw 'GitHub credential is not available; authenticate Git for this repository'}
$token=$passwordLine.Substring(9)
$headers=@{Authorization="Bearer $token";'User-Agent'='ByPass-release';Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
$api="https://api.github.com/repos/$repository"
try {
    $runs=Invoke-RestMethod -Uri "$api/actions/workflows/release.yml/runs?head_sha=$commit&per_page=5" -Headers $headers
    if(-not ($runs.workflow_runs | Where-Object {$_.head_sha -eq $commit -and $_.event -ne 'pull_request' -and $_.status -eq 'completed' -and $_.conclusion -eq 'success'})){throw 'The release workflow for this exact commit has not passed yet'}
    $releases=Invoke-RestMethod -Uri "$api/releases?per_page=50" -Headers $headers
    $release=$releases | Where-Object {$_.tag_name -eq $signed.tag_name} | Select-Object -First 1
    if(-not $release){throw 'The release draft was not found'}
    if(-not $release.draft){throw 'Release is already public; published assets are never replaced'}
    try{$ref=Invoke-RestMethod -Uri "$api/git/ref/tags/$($signed.tag_name)" -Headers $headers}
    catch {
        if([int]$_.Exception.Response.StatusCode -ne 404){throw}
        if($release.target_commitish -ne $commit){throw 'Draft release does not identify the reviewed commit'}
        $ref=Invoke-RestMethod -Method Post -Uri "$api/git/refs" -Headers $headers -ContentType 'application/json' -Body (@{ref=('refs/tags/'+$signed.tag_name);sha=$commit} | ConvertTo-Json)
    }
    if($ref.object.type -ne 'commit' -or $ref.object.sha -ne $commit){throw 'Release tag does not point to the reviewed commit'}
    $names=@('ByPass-win-x64.zip','ByPass-win-x64.zip.sha256.txt',$signed.assets[0].name,($signed.assets[0].name+'.sha256.txt'),'update.json','security-report.json','defender-scan.log','build-proof.json')
    foreach($name in $names) {
        $path=Join-Path $ReleaseDirectory $name;if(-not(Test-Path -LiteralPath $path)){throw "Missing release asset: $name"}
        foreach($old in @($release.assets | Where-Object {$_.name -eq $name})){Invoke-RestMethod -Method Delete -Uri "$api/releases/assets/$($old.id)" -Headers $headers | Out-Null}
        $upload=$release.upload_url.Split('{')[0]+'?name='+[Uri]::EscapeDataString($name)
        $asset=Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -InFile $path -ContentType 'application/octet-stream'
        if($asset.size -ne (Get-Item -LiteralPath $path).Length -or ($asset.digest -and $asset.digest -ne 'sha256:'+(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant())){throw 'Uploaded asset failed hash/size verification'}
    }
    foreach($old in @($release.assets | Where-Object {$_.name -eq 'update-unsigned.json'})){Invoke-RestMethod -Method Delete -Uri "$api/releases/assets/$($old.id)" -Headers $headers | Out-Null}
    $updated=Invoke-RestMethod -Uri "$api/releases/$($release.id)" -Headers $headers
    foreach($name in $names){if(@($updated.assets | Where-Object {$_.name -eq $name}).Count -ne 1){throw 'Release draft is incomplete'}}
    $published=Invoke-RestMethod -Method Patch -Uri "$api/releases/$($release.id)" -Headers $headers -ContentType 'application/json' -Body (@{draft=$false;make_latest='true'} | ConvertTo-Json)
    if($published.draft){throw 'Release remained a draft'}
    Write-Output "Published: $($published.html_url)"
}finally{$token=$null;$passwordLine=$null;$credential=$null;$headers.Clear()}
