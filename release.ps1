param(
    [string]$CommitMessage,
    [string[]]$Files,
    [ValidateRange(1,120)][int]$TimeoutMinutes=30
)
$ErrorActionPreference='Stop'
if($PSVersionTable.PSVersion.Major -lt 7){throw 'Run this script with PowerShell 7'}
$root=$PSScriptRoot
. "$root\tools\release-flow.ps1"
. "$root\tools\signing.ps1"
$repository='xxXdemidXxx/discord_obhod_app'
$api="https://api.github.com/repos/$repository"
$headers=$null;$credential=$null;$passwordLine=$null;$token=$null;$locked=$false
$sha=[Security.Cryptography.SHA256]::Create()
try{$lockId=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant())))).Replace('-','')}finally{$sha.Dispose()}
$mutex=[Threading.Mutex]::new($false,('Local\ByPass-OwnerRelease-'+$lockId))
try {
    try{$locked=$mutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$locked=$true}
    if(-not $locked){throw 'Another local release is in progress'}
    $branch=git -C $root branch --show-current
    if($LASTEXITCODE -ne 0 -or $branch -ne 'main'){throw 'Release from the official main checkout'}
    $remote=git -C $root remote get-url origin
    if($LASTEXITCODE -ne 0 -or $remote -notin @("https://github.com/$repository.git","https://github.com/$repository","git@github.com:$repository.git")){throw 'Origin does not identify the official repository'}
    $versionMatch=[regex]::Match((Get-Content -LiteralPath "$root\src\Version.cs" -Raw),'Value = "([0-9.]+)"')
    if(-not $versionMatch.Success){throw 'Application version was not found'}
    $version=([version]$versionMatch.Groups[1].Value).ToString(3)
    $tag="v$version"
    if(($Files -and -not $CommitMessage) -or ($CommitMessage -and -not $Files)){throw 'For a new commit supply both CommitMessage and an explicit Files list. For an existing clean commit omit both.'}
    $commitFiles=@(Resolve-ByPassReleaseFiles -Root $root -Files $Files)
    if(-not $Files -and (git -C $root status --porcelain)){throw 'Commit the intended changes or supply their explicit Files list; never stage unrelated changes'}
    # Check access before tests, commits, uploads or pushes. Never regenerate,
    # export or print the signing key, a backup password or Git credential.
    $key=Get-ByPassSigningKey
    try {
        $public=$key.ToXmlString($false)
        $sha=[Security.Cryptography.SHA256]::Create()
        try{$fingerprint=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($public)))).Replace('-','').ToLowerInvariant()}finally{$sha.Dispose()}
        if($fingerprint -ne '813c22d95ae9b8e82d7776a5c8c87acb47c05bf3d0cfa6137ceeeb915b4e125f'){throw 'Signing key differs from the official ByPass key'}
        $challenge=[Text.Encoding]::UTF8.GetBytes([guid]::NewGuid().ToString())
        $signature=$key.SignData($challenge,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'))
        if(-not $key.VerifyData($challenge,[Security.Cryptography.CryptoConfig]::MapNameToOID('SHA256'),$signature)){throw 'Local signing check failed'}
    }finally{$key.Dispose()}
    $credential=('protocol=https'+"`n"+'host=github.com'+"`n`n" | git -C $root credential fill)
    $passwordLine=$credential | Where-Object {$_.StartsWith('password=')} | Select-Object -First 1
    if(-not $passwordLine){throw 'GitHub authorization is missing. Authenticate Git locally; do not send credentials in chat.'}
    $token=$passwordLine.Substring(9)
    $headers=@{Authorization="Bearer $token";'User-Agent'='ByPass-owner-release';Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
    $latest=Invoke-RestMethod -Uri "$api/releases/latest" -Headers $headers
    if([version]$version -le [version]$latest.tag_name.TrimStart('v')){throw "Version $version is already published or older. Increase AppVersion and release notes before releasing."}
    git -C $root fetch origin main
    if($LASTEXITCODE -ne 0){throw 'Failed to fetch current main'}
    git -C $root merge-base --is-ancestor origin/main HEAD
    if($LASTEXITCODE -ne 0){throw 'Local main is behind or diverged. Incorporate origin/main before releasing.'}
    Write-Host "1/6: Tests for ByPass $version"
    & "$root\test.ps1"
    if($Files) {
        $alreadyStaged=@(git -C $root diff --cached --name-only)
        foreach($path in $alreadyStaged){if($path -notin $commitFiles){throw "Unrelated file is already staged: $path"}}
        $literalPaths=@($commitFiles | ForEach-Object {':(literal)'+$_})
        git -C $root add -- @literalPaths
        if($LASTEXITCODE -ne 0){throw 'Failed to stage reviewed source files'}
        if((git -C $root diff --name-only) -or (git -C $root ls-files --others --exclude-standard)){throw 'Unselected changes remain. Review them before committing; nothing has been pushed.'}
        git -C $root diff --cached --quiet
        if($LASTEXITCODE -eq 1){git -C $root commit -m $CommitMessage;if($LASTEXITCODE -ne 0){throw 'Commit failed'}}
        elseif($LASTEXITCODE -ne 0){throw 'Could not inspect staged changes'}
    }
    if(git -C $root status --porcelain){throw 'Source tree is not clean after tests and commit'}
    $commit=git -C $root rev-parse HEAD
    if($LASTEXITCODE -ne 0){throw 'Source commit was not found'}
    Write-Host "2/6: Push reviewed commit $commit"
    git -C $root push origin HEAD:main
    if($LASTEXITCODE -ne 0){throw 'Push failed; release was not published'}
    Write-Host '3/6: Local package with owner signature'
    & "$root\package-friends.ps1" -OwnerSignature
    Write-Host '4/6: Microsoft Defender scan and signed update manifest'
    & "$root\tools\scan-release.ps1"
    & "$root\tools\sign-release.ps1"
    Write-Host '5/6: Wait for successful GitHub Actions for this exact commit'
    $run=Wait-ByPassReleaseWorkflow -Api $api -Headers $headers -Commit $commit -TimeoutMinutes $TimeoutMinutes
    & "$root\tools\publish-release.ps1"
    Write-Host '6/6: Verify the public release and downloaded files'
    $release=Invoke-RestMethod -Uri "$api/releases/tags/$tag" -Headers $headers
    Assert-ByPassPublishedRelease -Release $release -Tag $tag -Commit $commit
    $ref=Invoke-RestMethod -Uri "$api/git/ref/tags/$tag" -Headers $headers
    if($ref.object.type -ne 'commit' -or $ref.object.sha -ne $commit){throw 'Public tag differs from the reviewed commit'}
    $check=Join-Path $root ('downloads\published-check-'+[guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $check | Out-Null
    foreach($name in @('ByPass-win-x64.zip',"Obhod-$version-win-x64.zip",'update.json','build-proof.json','security-report.json')) {
        $asset=@($release.assets | Where-Object name -eq $name)
        if($asset.Count -ne 1){throw "Public asset is missing or duplicated: $name"}
        $url=$asset[0].browser_download_url
        if($url -ne "https://github.com/$repository/releases/download/$tag/$name"){throw 'Unexpected public download source'}
        Invoke-WebRequest -Uri $url -OutFile (Join-Path $check $name)
        if((Get-FileHash -LiteralPath (Join-Path $check $name)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root "releases\$name")).Hash){throw "Downloaded file differs from the signed local release: $name"}
    }
    Expand-Archive -LiteralPath (Join-Path $check 'ByPass-win-x64.zip') -DestinationPath (Join-Path $check 'unpacked')
    & "$root\tools\verify-build.ps1" -Executable (Join-Path $check 'unpacked\ByPass\ByPass.exe')
    Write-Host "Released and verified: $($release.html_url)"
}finally {
    if($headers){$headers.Clear()}
    $token=$null;$passwordLine=$null;$credential=$null
    if($locked){$mutex.ReleaseMutex()};$mutex.Dispose()
}
