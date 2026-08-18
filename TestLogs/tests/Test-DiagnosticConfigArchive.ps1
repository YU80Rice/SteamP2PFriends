Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path -Path $PSScriptRoot -ChildPath '..\TestLogCommon.ps1')

function Assert-True {
    param([Parameter(Mandatory = $true)][bool] $Condition, [Parameter(Mandatory = $true)][string] $Message)
    if (-not $Condition) { throw $Message }
}

$fixtureRoot = Join-Path -Path ([System.IO.Path]::GetTempPath()) -ChildPath ('SteamP2PFriends-TestLogs-' + [guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
    $configPath = Join-Path -Path $fixtureRoot -ChildPath 'com.yu80rice.steamp2pfriends.cfg'
    [System.IO.File]::WriteAllText($configPath, "[Debug]`r`nVerboseDiagnostics = false`r`nRouteDiagnostics = false`r`n", (New-Object System.Text.UTF8Encoding($false)))

    $snapshot = Get-SteamP2PFriendsDiagnosticConfiguration -ConfigPath $configPath
    Assert-True -Condition $snapshot.IsDefaultDiagnostic -Message 'Expected the default diagnostic profile.'
    Assert-True -Condition (-not $snapshot.VerboseDiagnostics) -Message 'VerboseDiagnostics must parse as false.'
    Assert-True -Condition (-not $snapshot.RouteDiagnostics) -Message 'RouteDiagnostics must parse as false.'

    $archivePath = Join-Path -Path $fixtureRoot -ChildPath 'archive.cfg'
    $record = Copy-FileWithHash -SourcePath $configPath -DestinationPath $archivePath -RelativePath 'configs/Host/com.yu80rice.steamp2pfriends.cfg'
    Assert-True -Condition ($record.CopySHA256 -eq $snapshot.SHA256) -Message 'Configuration archive hash must match its snapshot.'

    [System.IO.File]::WriteAllText($configPath, "[Debug]`r`nVerboseDiagnostics = false`r`nRouteDiagnostics = true`r`n", (New-Object System.Text.UTF8Encoding($false)))
    $nonDefaultSnapshot = Get-SteamP2PFriendsDiagnosticConfiguration -ConfigPath $configPath
    Assert-True -Condition (-not $nonDefaultSnapshot.IsDefaultDiagnostic) -Message 'RouteDiagnostics=true must reject the default profile.'

    [System.IO.File]::WriteAllText($configPath, "[Debug]`r`nVerboseDiagnostics = false`r`nVerboseDiagnostics = true`r`nRouteDiagnostics = false`r`n", (New-Object System.Text.UTF8Encoding($false)))
    $duplicateRejected = $false
    try { Get-SteamP2PFriendsDiagnosticConfiguration -ConfigPath $configPath | Out-Null } catch { $duplicateRejected = $true }
    Assert-True -Condition $duplicateRejected -Message 'Duplicate diagnostic keys must fail closed.'

    Write-Output 'PASS Test-DiagnosticConfigArchive'
}
finally {
    if (Test-Path -LiteralPath $fixtureRoot -PathType Container) {
        Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
    }
}
