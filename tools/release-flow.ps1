$ErrorActionPreference='Stop'

function Resolve-ByPassReleaseFiles {
    param([string]$Root,[string[]]$Files)
    $prefix=[IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    foreach($file in $Files) {
        $full=[IO.Path]::GetFullPath((Join-Path $Root $file))
        if(-not $full.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Commit paths must stay inside the repository'}
        $relative=$full.Substring($prefix.Length).Replace('\','/')
        if($relative -match '(^|/)(\.git|downloads|dist|releases|fixture-[^/]*)(/|$)' -or $relative -match '(?i)\.(key|pfx|p12|private\.xml|key-backup\.json)$'){throw "Do not commit private keys, backups or build artifacts: $relative"}
        $relative
    }
}

function Get-ByPassReleaseRun {
    param($Runs,[string]$Commit)
    $Runs | Where-Object {$_.head_sha -eq $Commit -and $_.head_branch -eq 'main' -and $_.event -in @('push','workflow_dispatch')} |
        Sort-Object created_at -Descending | Select-Object -First 1
}

function Wait-ByPassReleaseWorkflow {
    param([string]$Api,[hashtable]$Headers,[string]$Commit,[int]$TimeoutMinutes=30)
    $deadline=[DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
    $previous=''
    while([DateTime]::UtcNow -lt $deadline) {
        $runs=Invoke-RestMethod -Uri "$Api/actions/workflows/release.yml/runs?head_sha=$Commit&per_page=20" -Headers $Headers
        $run=Get-ByPassReleaseRun $runs.workflow_runs $Commit
        $state=if($run){"$($run.id): $($run.status) $($run.conclusion)"}else{'Waiting for GitHub Actions to start'}
        if($state -ne $previous){Write-Host "Actions: $state";if($run){Write-Host $run.html_url};$previous=$state}
        if($run -and $run.status -eq 'completed') {
            if($run.conclusion -ne 'success'){throw "Release workflow failed: $($run.html_url). Nothing will be published."}
            return $run
        }
        Start-Sleep -Seconds 15
    }
    throw 'Timed out waiting for Actions. Fix or finish the workflow, then rerun release.ps1 from this clean commit.'
}

function Assert-ByPassPublishedRelease {
    param($Release,[string]$Tag,[string]$Commit)
    if($Release.tag_name -ne $Tag -or $Release.draft -or $Release.prerelease -or -not $Release.immutable){throw 'Published release identity or immutability check failed'}
    # GitHub may return a branch in target_commitish, so the exact tag is checked
    # separately against the commit before the assets are downloaded.
    if(-not $Commit -or $Commit -notmatch '^[a-f0-9]{40}$'){throw 'Invalid reviewed commit'}
}
