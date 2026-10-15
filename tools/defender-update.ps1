$ErrorActionPreference='Stop'

function Update-ByPassDefenderSignatures {
    param(
        [Parameter(Mandatory)][scriptblock]$PowerShellUpdate,
        [Parameter(Mandatory)][scriptblock]$CommandLineUpdate,
        [scriptblock]$Delay={Start-Sleep -Seconds 5}
    )
    for($attempt=1;$attempt -le 3;$attempt++) {
        try { & $PowerShellUpdate; return }
        catch { Write-Warning "Defender PowerShell update attempt ${attempt}: $($_.Exception.Message)" }
        # The native Microsoft client avoids a failing PowerShell/CIM RPC path.
        # A failed native update is still fatal after bounded retries.
        try { & $CommandLineUpdate; return }
        catch { Write-Warning "Defender command-line update attempt ${attempt}: $($_.Exception.Message)" }
        if($attempt -lt 3){ & $Delay }
    }
    throw 'Microsoft Defender signature update failed through both supported clients; release must not continue.'
}
