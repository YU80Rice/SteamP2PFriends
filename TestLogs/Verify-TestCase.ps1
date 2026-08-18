[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string] $CaseId,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string] $ExpectedPluginSha256
)

. (Join-Path -Path $PSScriptRoot -ChildPath 'TestLogCommon.ps1')

$caseDir = Get-CaseDirectory -CaseId $CaseId
if (-not (Test-Path -LiteralPath $caseDir -PathType Container)) {
    throw "Test case was not found: $caseDir"
}

$allOk = $true
$roleChecks = @()
$fileChecks = @()
$declaredFiles = @()

foreach ($role in @('Host', 'Client')) {
    $initPath = Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'roles' -ChildPath (Join-Path -Path $role -ChildPath 'case-init.json'))
    $prePath = Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'logs' -ChildPath (Join-Path -Path $role -ChildPath 'pre\snapshot.json'))
    $postPath = Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'logs' -ChildPath (Join-Path -Path $role -ChildPath 'post\snapshot.json'))
    $init = $null
    $pre = $null
    $post = $null
    if (Test-Path -LiteralPath $initPath -PathType Leaf) { $init = Read-JsonFile -Path $initPath }
    if (Test-Path -LiteralPath $prePath -PathType Leaf) { $pre = Read-JsonFile -Path $prePath }
    if (Test-Path -LiteralPath $postPath -PathType Leaf) { $post = Read-JsonFile -Path $postPath }

    $pluginMatches = $false
    if ($null -ne $init -and $null -ne $init.PluginDll -and $null -ne $init.PluginDll.SHA256) {
        $pluginMatches = ([string]$init.PluginDll.SHA256).ToUpperInvariant() -eq $ExpectedPluginSha256.ToUpperInvariant()
    }
    $prePresent = $null -ne $pre -and @($pre.Files).Count -eq 2
    $postPresent = $null -ne $post -and @($post.Files).Count -eq 2
    $roleOk = $null -ne $init -and $pluginMatches -and $prePresent -and $postPresent
    if (-not $roleOk) { $allOk = $false }
    $roleChecks += [pscustomobject]@{
        Role              = $role
        Initialization    = $null -ne $init
        PluginHashMatches = $pluginMatches
        PreLogSnapshot    = $prePresent
        PostLogSnapshot   = $postPresent
        Status            = $(if ($roleOk) { 'OK' } else { 'ROLE_EVIDENCE_FAIL' })
    }

    foreach ($snapshot in @($pre, $post)) {
        if ($null -ne $snapshot) { $declaredFiles += @($snapshot.Files) }
    }
    foreach ($extraPath in @(
        (Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'screenshots' -ChildPath (Join-Path -Path $role -ChildPath 'screenshots.json'))),
        (Join-Path -Path $caseDir -ChildPath (Join-Path -Path 'fingerprints' -ChildPath (Join-Path -Path $role -ChildPath 'fingerprint.json')))
    )) {
        if (Test-Path -LiteralPath $extraPath -PathType Leaf) {
            $declaredFiles += @(Read-JsonFile -Path $extraPath).Files
        }
    }
}

foreach ($file in $declaredFiles) {
    if (-not [bool]$file.Exists) {
        $fileChecks += [pscustomobject]@{ RelativePath = $file.RelativePath; Status = 'NOT_PRESENT_AS_DECLARED' }
        continue
    }
    if ([string]::IsNullOrWhiteSpace([string]$file.RelativePath) -or $file.RelativePath.Contains('..')) {
        throw 'Invalid evidence relative path in manifest.'
    }
    $path = Join-Path -Path $caseDir -ChildPath $file.RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $fileChecks += [pscustomobject]@{ RelativePath = $file.RelativePath; Status = 'MISSING' }
        $allOk = $false
        continue
    }
    $item = Get-Item -LiteralPath $path -ErrorAction Stop
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
    $ok = $hash -eq $file.CopySHA256 -and $item.Length -eq [long]$file.SizeBytes
    if (-not $ok) { $allOk = $false }
    $fileChecks += [pscustomobject]@{ RelativePath = $file.RelativePath; Status = $(if ($ok) { 'OK' } else { 'HASH_OR_SIZE_MISMATCH' }); ExpectedSHA256 = $file.CopySHA256; ActualSHA256 = $hash; ExpectedSize = $file.SizeBytes; ActualSize = $item.Length }
}

$verificationPath = Join-Path -Path $caseDir -ChildPath 'verification.json'
Write-JsonNoOverwrite -Path $verificationPath -Value ([pscustomobject]@{
    CaseId               = $CaseId
    VerifiedTimeUtc      = [DateTime]::UtcNow.ToString('o')
    ExpectedPluginSha256 = $ExpectedPluginSha256.ToUpperInvariant()
    AllOK                = $allOk
    RoleChecks           = $roleChecks
    FileChecks           = $fileChecks
})

if (-not $allOk) {
    Write-Output "CASE VERIFY FAIL: $CaseId"
    exit 1
}

Write-Output "CASE VERIFY OK: $CaseId ($($fileChecks.Count) files verified)"
