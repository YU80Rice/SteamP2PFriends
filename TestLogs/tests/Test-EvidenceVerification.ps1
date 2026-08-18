Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path -Path $PSScriptRoot -ChildPath '..\TestLogCommon.ps1')

function Assert-True {
    param([Parameter(Mandatory = $true)][bool] $Condition, [Parameter(Mandatory = $true)][string] $Message)
    if (-not $Condition) { throw $Message }
}

function Write-TestFileRecord {
    param(
        [Parameter(Mandatory = $true)][string] $CaseDirectory,
        [Parameter(Mandatory = $true)][string] $RelativePath,
        [Parameter(Mandatory = $true)][string] $Content
    )

    $path = Join-Path -Path $CaseDirectory -ChildPath $RelativePath
    Ensure-Directory -Path (Split-Path -Path $path -Parent)
    [System.IO.File]::WriteAllText($path, $Content, (New-Object System.Text.UTF8Encoding($false)))
    $item = Get-Item -LiteralPath $path -ErrorAction Stop
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
    return [pscustomobject]@{
        SourcePath       = $path
        RelativePath     = $RelativePath
        Exists           = $true
        SizeBytes        = [long]$item.Length
        SourceSHA256     = $hash
        CopySHA256       = $hash
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
    }
}

function Write-EvidenceJson {
    param([Parameter(Mandatory = $true)][string] $Path, [Parameter(Mandatory = $true)][object] $Value)

    Ensure-Directory -Path (Split-Path -Path $Path -Parent)
    [System.IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($true)))
}

function New-TestRoleEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $CaseDirectory,
        [Parameter(Mandatory = $true)][string] $RequestedCaseId,
        [Parameter(Mandatory = $true)][ValidateSet('Host', 'Client')][string] $Role,
        [Parameter(Mandatory = $true)][ValidateSet('Default')][string] $Profile,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string] $PluginHash,
        [string] $RecordCaseId = $RequestedCaseId,
        [bool] $DetachConfigHash = $false
    )

    $configRecord = Write-TestFileRecord -CaseDirectory $CaseDirectory -RelativePath ("configs/{0}/com.yu80rice.steamp2pfriends.cfg" -f $Role) -Content "[Debug]`r`nVerboseDiagnostics = false`r`nRouteDiagnostics = false`r`n"
    if ($DetachConfigHash) {
        $configRecord.SourceSHA256 = (('B' * 64) -join '')
    }
    $configuration = [pscustomobject]@{
        SourcePath         = $configRecord.SourcePath
        SizeBytes          = $configRecord.SizeBytes
        SHA256             = $configRecord.CopySHA256
        LastWriteTimeUtc   = $configRecord.LastWriteTimeUtc
        VerboseDiagnostics = $false
        RouteDiagnostics   = $false
        IsDefaultDiagnostic = $true
    }

    $plugin = [pscustomobject]@{ Path = 'SteamP2PFriends.dll'; SizeBytes = [long]1; SHA256 = $PluginHash; Version = '0.2.3.61'; MVID = '00000000-0000-0000-0000-000000000001' }
    $start = [pscustomobject]@{
        Schema = 'SteamP2PFriendsRuntimeEvidenceV1'
        CaseId = $RecordCaseId
        Role = $Role
        DiagnosticProfile = $Profile
        PluginDll = $plugin
        DiagnosticConfiguration = [pscustomobject]@{ AtStart = $configuration; Archive = $configRecord }
    }
    $finish = [pscustomobject]@{
        Schema = 'SteamP2PFriendsRuntimeEvidenceV1'
        CaseId = $RecordCaseId
        Role = $Role
        PluginAtFinish = $plugin
        DiagnosticConfigurationAtFinish = $configuration
    }
    $roleDirectory = Join-Path -Path $CaseDirectory -ChildPath ("roles/{0}" -f $Role)
    Write-EvidenceJson -Path (Join-Path -Path $roleDirectory -ChildPath 'start.json') -Value $start
    Write-EvidenceJson -Path (Join-Path -Path $roleDirectory -ChildPath 'finish.json') -Value $finish
}

function New-TestCase {
    param(
        [Parameter(Mandatory = $true)][string] $CaseId,
        [ValidateSet('Default')][string] $HostProfile = 'Default',
        [ValidateSet('Default')][string] $ClientProfile = 'Default',
        [string] $HostRecordCaseId = $CaseId,
        [string] $HostPluginHash = $(('A' * 64) -join ''),
        [bool] $DetachHostConfigHash = $false
    )

    $caseDirectory = Get-CaseDirectory -CaseId $CaseId
    Ensure-Directory -Path $caseDirectory
    New-TestRoleEvidence -CaseDirectory $caseDirectory -RequestedCaseId $CaseId -Role Host -Profile $HostProfile -PluginHash $HostPluginHash -RecordCaseId $HostRecordCaseId -DetachConfigHash $DetachHostConfigHash
    New-TestRoleEvidence -CaseDirectory $caseDirectory -RequestedCaseId $CaseId -Role Client -Profile $ClientProfile -PluginHash $HostPluginHash
}

function Assert-VerifyResult {
    param([Parameter(Mandatory = $true)][string] $CaseId, [Parameter(Mandatory = $true)][bool] $ExpectedPass)

    $engine = Join-Path -Path $PSScriptRoot -ChildPath '..\SteamP2PFriends-TestEvidence.ps1'
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $engine -Action Verify -CaseId $CaseId -DiagnosticProfile Default 2>&1
    $actualPass = $LASTEXITCODE -eq 0
    if ($actualPass -ne $ExpectedPass) {
        throw "VERIFY result mismatch for $CaseId. ExpectedPass=$ExpectedPass Output=$($output -join [Environment]::NewLine)"
    }
}

$caseIds = New-Object 'System.Collections.Generic.List[string]'
try {
    $prefix = 'ToolVerify-' + [guid]::NewGuid().ToString('N').Substring(0, 12)

    $validCase = "$prefix-Pass"
    $caseIds.Add($validCase)
    New-TestCase -CaseId $validCase
    Assert-VerifyResult -CaseId $validCase -ExpectedPass $true

    $wrongCase = "$prefix-WrongCase"
    $caseIds.Add($wrongCase)
    New-TestCase -CaseId $wrongCase -HostRecordCaseId "$wrongCase-Other"
    Assert-VerifyResult -CaseId $wrongCase -ExpectedPass $false

    $emptyHashCase = "$prefix-EmptyHash"
    $caseIds.Add($emptyHashCase)
    New-TestCase -CaseId $emptyHashCase -HostPluginHash ''
    Assert-VerifyResult -CaseId $emptyHashCase -ExpectedPass $false

    $configMismatchCase = "$prefix-ConfigHash"
    $caseIds.Add($configMismatchCase)
    New-TestCase -CaseId $configMismatchCase -DetachHostConfigHash $true
    Assert-VerifyResult -CaseId $configMismatchCase -ExpectedPass $false

    $engine = Join-Path -Path $PSScriptRoot -ChildPath '..\SteamP2PFriends-TestEvidence.ps1'
    $profileProcess = Start-Process -FilePath 'powershell.exe' -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $engine, '-Action', 'Verify', '-CaseId', $validCase, '-DiagnosticProfile', 'Any') -PassThru -Wait -WindowStyle Hidden
    if ($profileProcess.ExitCode -eq 0) { throw 'DiagnosticProfile Any must be rejected.' }

    Write-Output 'PASS Test-EvidenceVerification'
}
finally {
    foreach ($caseId in $caseIds) {
        $caseDirectory = Get-CaseDirectory -CaseId $caseId
        if (Test-Path -LiteralPath $caseDirectory -PathType Container) {
            Remove-Item -LiteralPath $caseDirectory -Recurse -Force
        }
    }
}
