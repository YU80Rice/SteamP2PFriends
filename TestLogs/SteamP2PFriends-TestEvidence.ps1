[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][ValidateSet('Start', 'Finish', 'Verify')][string] $Action,
    [Parameter(Mandatory = $true)][string] $CaseId,
    [ValidateSet('Host', 'Client')][string] $Role,
    [ValidateSet('Default')][string] $DiagnosticProfile = 'Default'
)

. (Join-Path -Path $PSScriptRoot -ChildPath 'TestLogCommon.ps1')

function Assert-GameClosed {
    if (Get-Process -Name 'Unturned' -ErrorAction SilentlyContinue) {
        throw 'Unturned is running. Exit the game completely before this step.'
    }
}

function Find-UnturnedGameRoot {
    $steamRoots = New-Object 'System.Collections.Generic.List[string]'
    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($registryLocation in @(
        [pscustomobject]@{ Key = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
        [pscustomobject]@{ Key = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
        [pscustomobject]@{ Key = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' }
    )) {
        try {
            $value = (Get-ItemProperty -LiteralPath $registryLocation.Key -Name $registryLocation.Name -ErrorAction Stop).($registryLocation.Name)
            if (-not [string]::IsNullOrWhiteSpace([string]$value)) {
                $path = [System.IO.Path]::GetFullPath(([string]$value).Replace('/', '\'))
                if ($seen.Add($path)) { $steamRoots.Add($path) }
            }
        } catch { }
    }
    if ($steamRoots.Count -eq 0) { throw 'Steam installation was not found in the Windows registry.' }

    $libraries = New-Object 'System.Collections.Generic.List[string]'
    foreach ($steamRoot in $steamRoots) {
        $libraries.Add($steamRoot)
        $libraryFile = Join-Path -Path $steamRoot -ChildPath 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $libraryFile -PathType Leaf)) { continue }
        foreach ($match in [regex]::Matches((Get-Content -LiteralPath $libraryFile -Raw -Encoding UTF8), '"path"\s+"(?<path>[^"]+)"')) {
            try {
                $library = [System.IO.Path]::GetFullPath($match.Groups['path'].Value.Replace('\\', '\').Replace('/', '\'))
                if ($seen.Add($library)) { $libraries.Add($library) }
            } catch { }
        }
    }
    foreach ($library in $libraries) {
        $gameRoot = Join-Path -Path $library -ChildPath 'steamapps\common\Unturned'
        $manifest = Join-Path -Path $library -ChildPath 'steamapps\appmanifest_304930.acf'
        if ((Test-Path -LiteralPath $manifest -PathType Leaf) -and (Test-Path -LiteralPath (Join-Path -Path $gameRoot -ChildPath 'Unturned.exe') -PathType Leaf)) {
            return (Resolve-Path -LiteralPath $gameRoot -ErrorAction Stop).Path
        }
    }
    foreach ($library in $libraries) {
        $gameRoot = Join-Path -Path $library -ChildPath 'steamapps\common\Unturned'
        if (Test-Path -LiteralPath (Join-Path -Path $gameRoot -ChildPath 'Unturned.exe') -PathType Leaf) {
            return (Resolve-Path -LiteralPath $gameRoot -ErrorAction Stop).Path
        }
    }
    throw 'Unturned AppID 304930 was not found in a Steam library configured on this machine.'
}

function Find-DeployedPluginDll {
    param([Parameter(Mandatory = $true)][string] $GameRoot)

    $pluginRoot = Join-Path -Path $GameRoot -ChildPath 'BepInEx\plugins'
    if (-not (Test-Path -LiteralPath $pluginRoot -PathType Container)) { throw "BepInEx plugins directory was not found: $pluginRoot" }
    $matches = @(Get-ChildItem -LiteralPath $pluginRoot -Filter 'SteamP2PFriends.dll' -File -Recurse -ErrorAction Stop)
    if ($matches.Count -ne 1) { throw "Expected exactly one deployed SteamP2PFriends.dll, found $($matches.Count)." }
    return $matches[0].FullName
}

function Get-RoleDirectory {
    param([Parameter(Mandatory = $true)][string] $CaseDirectory, [Parameter(Mandatory = $true)][string] $CurrentRole)
    return (Join-Path -Path $CaseDirectory -ChildPath (Join-Path -Path 'roles' -ChildPath $CurrentRole))
}

function Get-RoleConfigurationDirectory {
    param([Parameter(Mandatory = $true)][string] $CaseDirectory, [Parameter(Mandatory = $true)][string] $CurrentRole)
    return (Join-Path -Path $CaseDirectory -ChildPath (Join-Path -Path 'configs' -ChildPath $CurrentRole))
}

function Assert-DefaultDiagnosticConfiguration {
    param([Parameter(Mandatory = $true)][object] $Configuration, [Parameter(Mandatory = $true)][string] $Phase)
    if (-not [bool]$Configuration.IsDefaultDiagnostic) {
        throw "$Phase requires VerboseDiagnostics=false and RouteDiagnostics=false."
    }
}

function Get-RecordValue {
    param([AllowNull()][object] $Record, [Parameter(Mandatory = $true)][string] $Name)
    if ($null -eq $Record) { return $null }
    $property = $Record.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    return $property.Value
}

function Test-BooleanValue {
    param([AllowNull()][object] $Value)
    return ($Value -is [bool])
}

function Test-ArchivedFileRecord {
    param([Parameter(Mandatory = $true)][string] $CaseDirectory, [AllowNull()][object] $Record)
    $relativePath = [string](Get-RecordValue -Record $Record -Name 'RelativePath')
    $copyHash = [string](Get-RecordValue -Record $Record -Name 'CopySHA256')
    $size = Get-RecordValue -Record $Record -Name 'SizeBytes'
    if ([string]::IsNullOrWhiteSpace($relativePath) -or $relativePath.Contains('..')) { return 'Archive record has an invalid relative path.' }
    if (-not (Test-Sha256Value -Value $copyHash)) { return "Archive record has an invalid SHA-256: $relativePath" }
    if ($null -eq $size -or -not ($size -is [byte] -or $size -is [int16] -or $size -is [int] -or $size -is [long]) -or [long]$size -lt 0) { return "Archive record has an invalid size: $relativePath" }
    $path = Join-Path -Path $CaseDirectory -ChildPath $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return "Archived file is missing: $relativePath" }
    $item = Get-Item -LiteralPath $path -ErrorAction Stop
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256 -ErrorAction Stop).Hash
    if ($hash -ne $copyHash -or $item.Length -ne [long]$size) { return "Archived file integrity mismatch: $relativePath" }
    return $null
}

function Add-DiagnosticSnapshotReasons {
    param([AllowNull()][object] $Snapshot, [Parameter(Mandatory = $true)][string] $Phase, [ref] $Reasons)
    if ($null -eq $Snapshot) {
        $Reasons.Value += "$Phase diagnostic configuration is missing."
        return
    }
    if (-not (Test-Sha256Value -Value ([string](Get-RecordValue -Record $Snapshot -Name 'SHA256')))) {
        $Reasons.Value += "$Phase diagnostic configuration SHA-256 is invalid."
    }
    foreach ($name in @('VerboseDiagnostics', 'RouteDiagnostics')) {
        if (-not (Test-BooleanValue -Value (Get-RecordValue -Record $Snapshot -Name $name))) {
            $Reasons.Value += "$Phase diagnostic configuration $name is not a Boolean."
        }
    }
}

function Start-CaseRole {
    if ([string]::IsNullOrWhiteSpace($Role)) { throw 'Role is required for Start.' }
    Assert-GameClosed
    $caseDirectory = Get-CaseDirectory -CaseId $CaseId
    $roleDirectory = Get-RoleDirectory -CaseDirectory $caseDirectory -CurrentRole $Role
    Ensure-Directory -Path $roleDirectory
    $startPath = Join-Path -Path $roleDirectory -ChildPath 'start.json'
    if (Test-Path -LiteralPath $startPath -PathType Leaf) { throw "$Role has already been started for this CaseId. Use Finish, or start a new case." }

    $gameRoot = Find-UnturnedGameRoot
    $pluginPath = Find-DeployedPluginDll -GameRoot $gameRoot
    $configPath = Get-SteamP2PFriendsConfigPath -GameRoot $gameRoot
    $diagnostics = Get-SteamP2PFriendsDiagnosticConfiguration -ConfigPath $configPath
    Assert-DefaultDiagnosticConfiguration -Configuration $diagnostics -Phase 'START'
    $configDirectory = Get-RoleConfigurationDirectory -CaseDirectory $caseDirectory -CurrentRole $Role
    Ensure-Directory -Path $configDirectory
    $configArchive = Copy-FileWithHash -SourcePath $configPath -DestinationPath (Join-Path -Path $configDirectory -ChildPath 'com.yu80rice.steamp2pfriends.cfg') -RelativePath ("configs/{0}/com.yu80rice.steamp2pfriends.cfg" -f $Role)
    if ($configArchive.CopySHA256 -ne [string]$diagnostics.SHA256) { throw 'Configuration archive hash did not match the START configuration snapshot.' }

    Write-JsonNoOverwrite -Path $startPath -Value ([pscustomobject]@{
        Schema = 'SteamP2PFriendsRuntimeEvidenceV1'; CaseId = $CaseId; Role = $Role; StartedTimeUtc = [DateTime]::UtcNow.ToString('o')
        PluginDll = Get-BinaryIdentity -Path $pluginPath
        DiagnosticProfile = $DiagnosticProfile
        DiagnosticConfiguration = [pscustomobject]@{ AtStart = $diagnostics; Archive = $configArchive }
    })
    Write-Output "START OK: $CaseId / $Role"
    Write-Output ("Plugin SHA-256 {0}" -f (Get-BinaryIdentity -Path $pluginPath).SHA256)
    Write-Output 'Start Unturned now. FINISH only after the game exits completely.'
}

function Finish-CaseRole {
    if ([string]::IsNullOrWhiteSpace($Role)) { throw 'Role is required for Finish.' }
    Assert-GameClosed
    $caseDirectory = Get-CaseDirectory -CaseId $CaseId
    $roleDirectory = Get-RoleDirectory -CaseDirectory $caseDirectory -CurrentRole $Role
    $startPath = Join-Path -Path $roleDirectory -ChildPath 'start.json'
    $finishPath = Join-Path -Path $roleDirectory -ChildPath 'finish.json'
    if (Test-Path -LiteralPath $finishPath -PathType Leaf) { throw "$Role has already been finished for this CaseId. Existing evidence was not overwritten." }
    $start = Read-JsonFile -Path $startPath
    $currentPlugin = Get-BinaryIdentity -Path ([string]$start.PluginDll.Path)
    if ($currentPlugin.SHA256 -ne [string]$start.PluginDll.SHA256) { throw 'The deployed plugin DLL changed after START. Create a new case after final deployment.' }
    $profile = [string]$start.DiagnosticProfile
    if ($profile -ne 'Default' -or $null -eq $start.DiagnosticConfiguration -or $null -eq $start.DiagnosticConfiguration.AtStart) { throw 'START record has no valid default diagnostic configuration evidence. Create a new case.' }
    $diagnostics = Get-SteamP2PFriendsDiagnosticConfiguration -ConfigPath ([string]$start.DiagnosticConfiguration.AtStart.SourcePath)
    Assert-DefaultDiagnosticConfiguration -Configuration $diagnostics -Phase 'FINISH'
    if ([string]$diagnostics.SHA256 -ne [string]$start.DiagnosticConfiguration.AtStart.SHA256) { throw 'SteamP2PFriends configuration changed after START. Create a new case.' }

    Write-JsonNoOverwrite -Path $finishPath -Value ([pscustomobject]@{
        Schema = 'SteamP2PFriendsRuntimeEvidenceV1'; CaseId = $CaseId; Role = $Role; FinishedTimeUtc = [DateTime]::UtcNow.ToString('o'); PluginAtFinish = $currentPlugin
        DiagnosticConfigurationAtFinish = $diagnostics
    })
    Write-Output "FINISH OK: $CaseId / $Role"
    Write-Output 'Plugin DLL and the START diagnostic configuration are unchanged. No game logs were copied or inspected.'
}

function Verify-Case {
    $caseDirectory = Get-CaseDirectory -CaseId $CaseId
    $reasons = @(); $roleChecks = @(); $identities = @{}
    foreach ($currentRole in @('Host', 'Client')) {
        $roleReasons = @(); $profile = $null
        $roleDirectory = Get-RoleDirectory -CaseDirectory $caseDirectory -CurrentRole $currentRole
        $startPath = Join-Path -Path $roleDirectory -ChildPath 'start.json'
        $finishPath = Join-Path -Path $roleDirectory -ChildPath 'finish.json'
        if (-not (Test-Path -LiteralPath $startPath -PathType Leaf) -or -not (Test-Path -LiteralPath $finishPath -PathType Leaf)) {
            $roleReasons += 'START or FINISH record is missing.'
        } else {
            try { $start = Read-JsonFile -Path $startPath; $finish = Read-JsonFile -Path $finishPath } catch { $roleReasons += "Unable to read evidence JSON: $($_.Exception.Message)"; $start = $null; $finish = $null }
            if ($null -ne $start -and $null -ne $finish) {
                foreach ($pair in @([pscustomobject]@{ Name='START'; Record=$start }, [pscustomobject]@{ Name='FINISH'; Record=$finish })) {
                    if ([string](Get-RecordValue -Record $pair.Record -Name 'Schema') -ne 'SteamP2PFriendsRuntimeEvidenceV1') { $roleReasons += "$($pair.Name) schema is invalid." }
                    if ([string](Get-RecordValue -Record $pair.Record -Name 'CaseId') -ne $CaseId) { $roleReasons += "$($pair.Name) CaseId does not match the requested case." }
                    if ([string](Get-RecordValue -Record $pair.Record -Name 'Role') -ne $currentRole) { $roleReasons += "$($pair.Name) role does not match its directory." }
                }
                $profile = [string](Get-RecordValue -Record $start -Name 'DiagnosticProfile')
                if ($profile -ne 'Default') { $roleReasons += 'START diagnostic profile must be Default.' }
                $startHash = [string](Get-RecordValue -Record (Get-RecordValue -Record $start -Name 'PluginDll') -Name 'SHA256')
                $finishHash = [string](Get-RecordValue -Record (Get-RecordValue -Record $finish -Name 'PluginAtFinish') -Name 'SHA256')
                if (-not (Test-Sha256Value -Value $startHash) -or -not (Test-Sha256Value -Value $finishHash)) { $roleReasons += 'START or FINISH plugin SHA-256 is missing or invalid.' } elseif ($startHash -ne $finishHash) { $roleReasons += 'Plugin DLL changed between START and FINISH.' } else { $identities[$currentRole] = $startHash }
                $config = Get-RecordValue -Record $start -Name 'DiagnosticConfiguration'
                $atStart = Get-RecordValue -Record $config -Name 'AtStart'
                $archive = Get-RecordValue -Record $config -Name 'Archive'
                $atFinish = Get-RecordValue -Record $finish -Name 'DiagnosticConfigurationAtFinish'
                Add-DiagnosticSnapshotReasons -Snapshot $atStart -Phase 'START' -Reasons ([ref]$roleReasons)
                Add-DiagnosticSnapshotReasons -Snapshot $atFinish -Phase 'FINISH' -Reasons ([ref]$roleReasons)
                if ($null -eq $archive) {
                    $roleReasons += 'START diagnostic configuration archive is missing.'
                } else {
                    $expectedConfigPath = "configs/{0}/com.yu80rice.steamp2pfriends.cfg" -f $currentRole
                    if ([string](Get-RecordValue -Record $archive -Name 'RelativePath') -ne $expectedConfigPath) { $roleReasons += 'START diagnostic configuration archive path is invalid.' }
                    $archiveReason = Test-ArchivedFileRecord -CaseDirectory $caseDirectory -Record $archive
                    if ($null -ne $archiveReason) { $roleReasons += $archiveReason }
                    $startConfigHash = [string](Get-RecordValue -Record $atStart -Name 'SHA256')
                    if ([string](Get-RecordValue -Record $archive -Name 'CopySHA256') -ne $startConfigHash -or [string](Get-RecordValue -Record $archive -Name 'SourceSHA256') -ne $startConfigHash) { $roleReasons += 'Archived diagnostic configuration hash does not match START.' }
                }
                if ($null -ne $atStart -and $null -ne $atFinish) {
                    if ([string](Get-RecordValue -Record $atStart -Name 'SHA256') -ne [string](Get-RecordValue -Record $atFinish -Name 'SHA256')) { $roleReasons += 'SteamP2PFriends configuration changed between START and FINISH.' }
                    foreach ($key in @('VerboseDiagnostics', 'RouteDiagnostics')) {
                        $startValue = Get-RecordValue -Record $atStart -Name $key; $finishValue = Get-RecordValue -Record $atFinish -Name $key
                        if ((Test-BooleanValue -Value $startValue) -and (Test-BooleanValue -Value $finishValue) -and [bool]$startValue -ne [bool]$finishValue) { $roleReasons += "Diagnostic configuration $key changed between START and FINISH." }
                    }
                    $verboseDiagnostics = Get-RecordValue -Record $atStart -Name 'VerboseDiagnostics'
                    $routeDiagnostics = Get-RecordValue -Record $atStart -Name 'RouteDiagnostics'
                    if (((Test-BooleanValue -Value $verboseDiagnostics) -and [bool]$verboseDiagnostics) -or ((Test-BooleanValue -Value $routeDiagnostics) -and [bool]$routeDiagnostics)) {
                        $roleReasons += 'Default diagnostic profile requires both diagnostic switches to be false.'
                    }
                }
            }
        }
        $status = if ($roleReasons.Count -eq 0) { 'PASS' } else { 'FAIL' }
        if ($status -eq 'FAIL') { $reasons += ("{0}: {1}" -f $currentRole, ($roleReasons -join ' ')) }
        $roleChecks += [pscustomobject]@{ Role=$currentRole; Status=$status; DiagnosticProfile=$profile; Reasons=$roleReasons }
    }
    $samePlugin = $identities.ContainsKey('Host') -and $identities.ContainsKey('Client') -and $identities['Host'] -eq $identities['Client']
    if (-not ($identities.ContainsKey('Host') -and $identities.ContainsKey('Client'))) { $reasons += 'Plugin DLL comparison is unavailable because one role has no complete evidence.' } elseif (-not $samePlugin) { $reasons += 'Host and Client plugin DLL SHA-256 values differ.' }
    $summaryPath = Join-Path -Path $caseDirectory -ChildPath 'evidence-summary.json'
    $allOk = $reasons.Count -eq 0
    Write-JsonNoOverwrite -Path $summaryPath -Value ([pscustomobject]@{ Schema='SteamP2PFriendsRuntimeEvidenceV1'; CaseId=$CaseId; VerifiedTimeUtc=[DateTime]::UtcNow.ToString('o'); AllOK=$allOk; SamePluginDll=$samePlugin; RoleChecks=$roleChecks; FailureReasons=$reasons })
    if (-not $allOk) { Write-Output "VERIFY FAIL: $CaseId"; $reasons | ForEach-Object { Write-Output ('- ' + $_) }; Write-Output "See $summaryPath"; exit 1 }
    Write-Output "VERIFY PASS: $CaseId"
    Write-Output 'Both roles used the same plugin DLL; their default diagnostic configuration snapshots are hash-verified.'
}

switch ($Action) {
    'Start' { Start-CaseRole }
    'Finish' { Finish-CaseRole }
    'Verify' { Verify-Case }
}
