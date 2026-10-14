$ErrorActionPreference='Stop'
. "$PSScriptRoot\..\tools\release-flow.ps1"
$releaseChecks=0
function Assert-ReleaseFlow($Condition,[string]$Name){if(-not $Condition){throw $Name};$script:releaseChecks++}
function Assert-ReleaseReject([scriptblock]$Action,[string]$Name){$rejected=$false;try{& $Action | Out-Null}catch{$rejected=$true};Assert-ReleaseFlow $rejected $Name}
$releaseRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$releasePaths=@(Resolve-ByPassReleaseFiles $releaseRoot @('src/Version.cs','security/update-public.xml','tools/release-flow.ps1'))
Assert-ReleaseFlow ($releasePaths.Count -eq 3 -and $releasePaths[0] -eq 'src/Version.cs') 'Explicit source files rejected'
foreach($path in @('../outside.txt','secrets/release.key','backup.key-backup.json','publisher.pfx','publisher.p12','key.private.xml','releases/update.json','downloads/archive.zip','.git/config','tests/fixture-example/key.xml')){
    Assert-ReleaseReject {Resolve-ByPassReleaseFiles $releaseRoot @($path)} "Unsafe commit path accepted: $path"
}
$releaseCommit='a'*40
$releaseRun=@(
    [pscustomobject]@{id=1;head_sha=('b'*40);head_branch='main';event='push';created_at='2026-10-12T10:00:00Z'},
    [pscustomobject]@{id=2;head_sha=$releaseCommit;head_branch='main';event='pull_request';created_at='2026-10-12T11:00:00Z'},
    [pscustomobject]@{id=3;head_sha=$releaseCommit;head_branch='main';event='push';created_at='2026-10-12T09:00:00Z'},
    [pscustomobject]@{id=4;head_sha=$releaseCommit;head_branch='other';event='workflow_dispatch';created_at='2026-10-12T12:00:00Z'}
)
Assert-ReleaseFlow ((Get-ByPassReleaseRun $releaseRun $releaseCommit).id -eq 3) 'Wrong commit, branch or pull request accepted'
Assert-ReleaseFlow ($null -eq (Get-ByPassReleaseRun $releaseRun ('c'*40))) 'Missing workflow accepted'
$releasePublished=[pscustomobject]@{tag_name='v0.5.3';draft=$false;prerelease=$false;immutable=$true}
Assert-ByPassPublishedRelease $releasePublished 'v0.5.3' $releaseCommit
$script:releaseChecks++
Assert-ReleaseReject {Assert-ByPassPublishedRelease $releasePublished 'v0.5.4' $releaseCommit} 'Wrong public tag accepted'
foreach($property in @('draft','prerelease')){$releasePublished.$property=$true;Assert-ReleaseReject {Assert-ByPassPublishedRelease $releasePublished 'v0.5.3' $releaseCommit} "$property accepted";$releasePublished.$property=$false}
$releasePublished.immutable=$false
Assert-ReleaseReject {Assert-ByPassPublishedRelease $releasePublished 'v0.5.3' $releaseCommit} 'Mutable release accepted'
$releasePublished.immutable=$true
Assert-ReleaseReject {Assert-ByPassPublishedRelease $releasePublished 'v0.5.3' 'not-a-commit'} 'Invalid commit accepted'
# Exercise the workflow gate without network or waiting. The publisher also
# rechecks the real GitHub workflow independently before touching the draft.
function Invoke-RestMethod {param($Uri,$Headers) [pscustomobject]@{workflow_runs=@([pscustomobject]@{id=5;head_sha=$releaseCommit;head_branch='main';event='push';status='completed';conclusion=$script:releaseConclusion;html_url='https://example.invalid/run';created_at='2026-10-12T13:00:00Z'})}}
try {
    $script:releaseConclusion='failure'
    Assert-ReleaseReject {Wait-ByPassReleaseWorkflow -Api 'https://example.invalid' -Headers @{} -Commit $releaseCommit -TimeoutMinutes 1} 'Failed workflow allowed publication'
    $script:releaseConclusion='success'
    Assert-ReleaseFlow ((Wait-ByPassReleaseWorkflow -Api 'https://example.invalid' -Headers @{} -Commit $releaseCommit -TimeoutMinutes 1).id -eq 5) 'Successful exact-commit workflow rejected'
}finally{Remove-Item Function:\Invoke-RestMethod}
$originalCulture=[Globalization.CultureInfo]::CurrentCulture
try {
    [Globalization.CultureInfo]::CurrentCulture=[Globalization.CultureInfo]::GetCultureInfo('ru-RU')
    $timestamp='2026-10-14T19:15:05.0000000Z';$expected=[DateTime]::new(2026,10,14,19,15,5,[DateTimeKind]::Utc)
    foreach($value in @($timestamp,([DateTimeOffset]$timestamp),$expected) | Where-Object {$null -ne $_}){Assert-ReleaseFlow ((ConvertTo-ByPassUtcTime $value) -eq $expected) 'Report timestamp changed in Russian locale'}
    $parsedReport=('{"scanned_at":"'+$timestamp+'"}' | ConvertFrom-Json)
    Assert-ReleaseFlow ((ConvertTo-ByPassUtcTime $parsedReport.scanned_at) -eq $expected) 'Auto-converted JSON timestamp rejected'
    Assert-ReleaseFlow ((ConvertTo-ByPassUtcTime '2026-10-14T22:15:05+03:00') -eq $expected) 'Explicit timestamp offset ignored'
    Assert-ReleaseReject {ConvertTo-ByPassUtcTime '10/14/2026 19:15:05'} 'Culture-dependent ambiguous timestamp accepted'
}finally{[Globalization.CultureInfo]::CurrentCulture=$originalCulture}
Write-Output "PASS: $releaseChecks release flow checks"
