[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $CaseId,
    [Parameter(Mandatory = $true)][ValidateSet('Host', 'Client')][string] $Role,
    [Parameter(Mandatory = $true)][string] $SavedataRoot,
    [Parameter(Mandatory = $true)][string] $ServerId,
    [Parameter(Mandatory = $true)][string] $MapName,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+$')][string] $SteamId,
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+$')][string] $CharacterId
)

. (Join-Path -Path $PSScriptRoot -ChildPath 'TestLogCommon.ps1')

$caseDir = Get-CaseDirectory -CaseId $CaseId
$initPath = Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'roles' -ChildPath (Join-Path -Path $Role -ChildPath 'case-init.json'))
if (-not (Test-Path -LiteralPath $initPath -PathType Leaf)) {
    throw "Role must be initialized before save fingerprints are captured: $Role"
}

$destinationDirectory = Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'fingerprints' -ChildPath $Role)
$worldDirectory = Join-Path -Path (Join-Path -Path $SavedataRoot -ChildPath 'Worlds') -ChildPath $ServerId
$playerSegment = '{0}_{1}' -f $SteamId, $CharacterId
$levelDirectory = Join-Path -Path (Join-Path -Path $worldDirectory -ChildPath 'Level') -ChildPath $MapName
$playerDirectory = Join-Path -Path (Join-Path -Path (Join-Path -Path $worldDirectory -ChildPath 'Players') -ChildPath $playerSegment) -ChildPath (Join-Path -Path $MapName -ChildPath 'Player')

$targets = @(
    [pscustomobject]@{ Source = (Join-Path -Path $levelDirectory -ChildPath 'Groups.dat'); Name = 'Groups.dat' },
    [pscustomobject]@{ Source = (Join-Path -Path $levelDirectory -ChildPath 'Barricades.dat'); Name = 'Barricades.dat' },
    [pscustomobject]@{ Source = (Join-Path -Path $levelDirectory -ChildPath 'Structures.dat'); Name = 'Structures.dat' },
    [pscustomobject]@{ Source = (Join-Path -Path $playerDirectory -ChildPath 'Player.dat'); Name = 'Player.dat' },
    [pscustomobject]@{ Source = (Join-Path -Path $playerDirectory -ChildPath 'Inventory.dat'); Name = 'Inventory.dat' },
    [pscustomobject]@{ Source = (Join-Path -Path $playerDirectory -ChildPath 'Quests.dat'); Name = 'Quests.dat' }
)

$records = @()
foreach ($target in $targets) {
    if (-not (Test-Path -LiteralPath $target.Source -PathType Leaf)) {
        $records += [pscustomobject]@{ SourcePath = $target.Source; RelativePath = ("fingerprints/{0}/{1}" -f $Role, $target.Name); Exists = $false; SizeBytes = 0; SourceSHA256 = ''; CopySHA256 = ''; LastWriteTimeUtc = '' }
        continue
    }
    $records += Copy-FileWithHash -SourcePath $target.Source -DestinationPath (Join-Path -Path $destinationDirectory -ChildPath $target.Name) -RelativePath ("fingerprints/{0}/{1}" -f $Role, $target.Name)
}

Write-JsonNoOverwrite -Path (Join-Path -Path $destinationDirectory -ChildPath 'fingerprint.json') -Value ([pscustomobject]@{
    CaseId          = $CaseId
    Role            = $Role
    CapturedTimeUtc = [DateTime]::UtcNow.ToString('o')
    SavedataRoot    = $SavedataRoot
    ServerId        = $ServerId
    MapName         = $MapName
    SteamId         = $SteamId
    CharacterId     = $CharacterId
    Files           = $records
})

Write-Output "FINGERPRINT OK: $CaseId/$Role"
