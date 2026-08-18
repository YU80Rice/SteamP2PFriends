[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $CaseId,
    [Parameter(Mandatory = $true)][ValidateSet('Host', 'Client')][string] $Role,
    [Parameter(Mandatory = $true)][ValidateSet('pre', 'post')][string] $Phase,
    [Parameter(Mandatory = $true)][string] $BepInExLogPath,
    [Parameter(Mandatory = $true)][string] $PlayerLogPath
)

. (Join-Path -Path $PSScriptRoot -ChildPath 'TestLogCommon.ps1')

if (Get-Process -Name 'Unturned' -ErrorAction SilentlyContinue) {
    throw 'Unturned is running. Archive logs only after the game exits completely.'
}

$caseDir = Get-CaseDirectory -CaseId $CaseId
$initPath = Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'roles' -ChildPath (Join-Path -Path $Role -ChildPath 'case-init.json'))
if (-not (Test-Path -LiteralPath $initPath -PathType Leaf)) {
    throw "Role must be initialized before a log snapshot: $Role"
}

$destinationDirectory = Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'logs' -ChildPath (Join-Path -Path $Role -ChildPath $Phase))
if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
    throw "Snapshot directory is absent: $destinationDirectory"
}

$records = @()
$records += Copy-FileWithHash -SourcePath $BepInExLogPath -DestinationPath (Join-Path -Path $destinationDirectory -ChildPath 'BepInEx-LogOutput.log') -RelativePath ("logs/{0}/{1}/BepInEx-LogOutput.log" -f $Role, $Phase)
$records += Copy-FileWithHash -SourcePath $PlayerLogPath -DestinationPath (Join-Path -Path $destinationDirectory -ChildPath 'Unity-Player.log') -RelativePath ("logs/{0}/{1}/Unity-Player.log" -f $Role, $Phase)

$snapshotPath = Join-Path -Path $destinationDirectory -ChildPath 'snapshot.json'
Write-JsonNoOverwrite -Path $snapshotPath -Value ([pscustomobject]@{
    CaseId          = $CaseId
    Role            = $Role
    Phase           = $Phase
    CapturedTimeUtc = [DateTime]::UtcNow.ToString('o')
    Files           = $records
})

Write-Output "SNAPSHOT OK: $CaseId/$Role/$Phase"
Write-Output "Manifest: $snapshotPath"
