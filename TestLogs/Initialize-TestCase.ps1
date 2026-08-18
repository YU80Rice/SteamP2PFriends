[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $CaseId,
    [Parameter(Mandatory = $true)][ValidateSet('Host', 'Client')][string] $Role,
    [Parameter(Mandatory = $true)][string] $PluginDll,
    [Parameter(Mandatory = $true)][string] $AssemblyCSharpDll,
    [Parameter(Mandatory = $true)][string] $BepInExCoreDll
)

. (Join-Path -Path $PSScriptRoot -ChildPath 'TestLogCommon.ps1')

$caseDir = Get-CaseDirectory -CaseId $CaseId
$roleDir = Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'roles' -ChildPath $Role)
$initPath = Join-Path -Path $roleDir -ChildPath 'case-init.json'
if (Test-Path -LiteralPath $initPath) {
    $existing = Read-JsonFile -Path $initPath
    $pluginIdentity = Get-BinaryIdentity -Path $PluginDll
    $assemblyIdentity = Get-BinaryIdentity -Path $AssemblyCSharpDll
    $bepInExIdentity = Get-BinaryIdentity -Path $BepInExCoreDll
    if ($existing.PluginDll.SHA256 -ne $pluginIdentity.SHA256 -or
        $existing.AssemblyCSharp.SHA256 -ne $assemblyIdentity.SHA256 -or
        $existing.BepInExCore.SHA256 -ne $bepInExIdentity.SHA256) {
        throw "Role is already initialized with different runtime identities: $Role"
    }
    Write-Output "INIT ALREADY PRESENT: $CaseId/$Role"
    Write-Output "Evidence: $roleDir"
    return
}

Ensure-Directory -Path $caseDir
Ensure-Directory -Path $roleDir
Ensure-Directory -Path (Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'logs' -ChildPath (Join-Path -Path $Role -ChildPath 'pre')))
Ensure-Directory -Path (Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'logs' -ChildPath (Join-Path -Path $Role -ChildPath 'post')))
Ensure-Directory -Path (Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'screenshots' -ChildPath $Role))
Ensure-Directory -Path (Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'fingerprints' -ChildPath $Role))

$record = [pscustomobject]@{
    CaseId          = $CaseId
    Role            = $Role
    CreatedTimeUtc  = [DateTime]::UtcNow.ToString('o')
    ArtifactRoot    = Get-TestLogArtifactRoot
    PluginDll       = Get-BinaryIdentity -Path $PluginDll
    AssemblyCSharp  = Get-BinaryIdentity -Path $AssemblyCSharpDll
    BepInExCore     = Get-BinaryIdentity -Path $BepInExCoreDll
}
Write-JsonNoOverwrite -Path $initPath -Value $record

Write-Output "INIT OK: $CaseId/$Role"
Write-Output "Evidence: $roleDir"
