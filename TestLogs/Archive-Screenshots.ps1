[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $CaseId,
    [Parameter(Mandatory = $true)][ValidateSet('Host', 'Client')][string] $Role,
    [Parameter(Mandatory = $true)][string[]] $ScreenshotPath
)

. (Join-Path -Path $PSScriptRoot -ChildPath 'TestLogCommon.ps1')

$caseDir = Get-CaseDirectory -CaseId $CaseId
$initPath = Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'roles' -ChildPath (Join-Path -Path $Role -ChildPath 'case-init.json'))
if (-not (Test-Path -LiteralPath $initPath -PathType Leaf)) {
    throw "Role must be initialized before screenshots are archived: $Role"
}

$destinationDirectory = Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'screenshots' -ChildPath $Role)
$records = @()
foreach ($inputPath in $ScreenshotPath) {
    $source = Require-File -Path $inputPath
    $leaf = Split-Path -Path $source -Leaf
    $records += Copy-FileWithHash -SourcePath $source -DestinationPath (Join-Path -Path $destinationDirectory -ChildPath $leaf) -RelativePath ("screenshots/{0}/{1}" -f $Role, $leaf)
}

Write-JsonNoOverwrite -Path (Join-Path -Path $destinationDirectory -ChildPath 'screenshots.json') -Value ([pscustomobject]@{
    CaseId          = $CaseId
    Role            = $Role
    CapturedTimeUtc = [DateTime]::UtcNow.ToString('o')
    Files           = $records
})

Write-Output "SCREENSHOTS OK: $CaseId/$Role"
