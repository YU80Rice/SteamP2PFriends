Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-TestLogArtifactRoot {
    $root = Join-Path -Path $PSScriptRoot -ChildPath 'artifacts'
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        New-Item -ItemType Directory -Path $root -Force | Out-Null
    }
    return (Resolve-Path -LiteralPath $root -ErrorAction Stop).Path
}

function Assert-CaseId {
    param([Parameter(Mandatory = $true)][string] $CaseId)

    if ($CaseId -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,79}$' -or $CaseId.EndsWith('.')) {
        throw 'CaseId must be 1-80 ASCII letters, digits, dot, underscore, or hyphen; it cannot begin or end with a dot.'
    }
}

function Get-CaseDirectory {
    param([Parameter(Mandatory = $true)][string] $CaseId)

    Assert-CaseId -CaseId $CaseId
    return (Join-Path -Path (Get-TestLogArtifactRoot) -ChildPath $CaseId)
}

function Ensure-Directory {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        New-Item -ItemType Directory -Path $Path -Force -ErrorAction Stop | Out-Null
    }
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "Directory was not created: $Path"
    }
}

function Require-File {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required file was not found: $Path"
    }
    return (Resolve-Path -LiteralPath $Path -ErrorAction Stop).Path
}

function Write-JsonNoOverwrite {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object] $Value
    )

    if (Test-Path -LiteralPath $Path) {
        throw "Refusing to overwrite evidence: $Path"
    }
    $json = $Value | ConvertTo-Json -Depth 12
    [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding($true)))
}

function Read-JsonFile {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Required JSON was not found: $Path"
    }
    return (Get-Content -LiteralPath $Path -Raw -Encoding UTF8 -ErrorAction Stop | ConvertFrom-Json)
}

function Copy-FileWithHash {
    param(
        [Parameter(Mandatory = $true)][string] $SourcePath,
        [Parameter(Mandatory = $true)][string] $DestinationPath,
        [Parameter(Mandatory = $true)][string] $RelativePath
    )

    $source = Require-File -Path $SourcePath
    if (Test-Path -LiteralPath $DestinationPath) {
        throw "Refusing to overwrite evidence: $DestinationPath"
    }

    $sourceItem = Get-Item -LiteralPath $source -ErrorAction Stop
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256 -ErrorAction Stop).Hash
    Copy-Item -LiteralPath $source -Destination $DestinationPath -ErrorAction Stop
    $destinationItem = Get-Item -LiteralPath $DestinationPath -ErrorAction Stop
    $destinationHash = (Get-FileHash -LiteralPath $DestinationPath -Algorithm SHA256 -ErrorAction Stop).Hash

    if ($destinationHash -ne $sourceHash -or $destinationItem.Length -ne $sourceItem.Length) {
        throw "Archive copy integrity failure: $source"
    }

    return [pscustomobject]@{
        SourcePath       = $source
        RelativePath     = $RelativePath
        Exists           = $true
        SizeBytes        = $destinationItem.Length
        SourceSHA256     = $sourceHash
        CopySHA256       = $destinationHash
        LastWriteTimeUtc = $sourceItem.LastWriteTimeUtc.ToString('o')
    }
}

function Get-SteamP2PFriendsConfigPath {
    param([Parameter(Mandatory = $true)][string] $GameRoot)

    $path = Join-Path -Path $GameRoot -ChildPath 'BepInEx\config\com.yu80rice.steamp2pfriends.cfg'
    return (Require-File -Path $path)
}

function Get-BepInExConfigValue {
    param(
        [Parameter(Mandatory = $true)][string] $ConfigPath,
        [Parameter(Mandatory = $true)][string] $Section,
        [Parameter(Mandatory = $true)][string] $Key
    )

    $config = Require-File -Path $ConfigPath
    $sectionPattern = '^\s*\[(?<section>[^\]]+)\]\s*(?:[#;].*)?$'
    $keyPattern = '^\s*' + [regex]::Escape($Key) + '\s*=\s*(?<value>.*?)\s*$'
    $currentSection = $null
    $values = New-Object 'System.Collections.Generic.List[string]'

    foreach ($line in Get-Content -LiteralPath $config -Encoding UTF8 -ErrorAction Stop) {
        $trimmed = $line.Trim()
        if ($trimmed.Length -eq 0 -or $trimmed.StartsWith('#') -or $trimmed.StartsWith(';')) {
            continue
        }

        $sectionMatch = [regex]::Match($line, $sectionPattern)
        if ($sectionMatch.Success) {
            $currentSection = $sectionMatch.Groups['section'].Value.Trim()
            continue
        }

        if (-not [string]::Equals($currentSection, $Section, [System.StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        $keyMatch = [regex]::Match($line, $keyPattern)
        if ($keyMatch.Success) {
            $values.Add($keyMatch.Groups['value'].Value.Trim())
        }
    }

    if ($values.Count -ne 1) {
        throw "Expected exactly one '$Key' value in [$Section]: $config"
    }

    return $values[0]
}

function Get-BepInExBooleanConfigValue {
    param(
        [Parameter(Mandatory = $true)][string] $ConfigPath,
        [Parameter(Mandatory = $true)][string] $Section,
        [Parameter(Mandatory = $true)][string] $Key
    )

    $value = Get-BepInExConfigValue -ConfigPath $ConfigPath -Section $Section -Key $Key
    if ([string]::Equals($value, 'true', [System.StringComparison]::OrdinalIgnoreCase)) { return $true }
    if ([string]::Equals($value, 'false', [System.StringComparison]::OrdinalIgnoreCase)) { return $false }
    throw "Expected '$Key' in [$Section] to be true or false: $ConfigPath"
}

function Get-SteamP2PFriendsDiagnosticConfiguration {
    param([Parameter(Mandatory = $true)][string] $ConfigPath)

    $config = Require-File -Path $ConfigPath
    $item = Get-Item -LiteralPath $config -ErrorAction Stop
    $verboseDiagnostics = Get-BepInExBooleanConfigValue -ConfigPath $config -Section 'Debug' -Key 'VerboseDiagnostics'
    $routeDiagnostics = Get-BepInExBooleanConfigValue -ConfigPath $config -Section 'Debug' -Key 'RouteDiagnostics'

    return [pscustomobject]@{
        SourcePath          = $config
        SizeBytes           = $item.Length
        SHA256              = (Get-FileHash -LiteralPath $config -Algorithm SHA256 -ErrorAction Stop).Hash
        LastWriteTimeUtc    = $item.LastWriteTimeUtc.ToString('o')
        VerboseDiagnostics  = $verboseDiagnostics
        RouteDiagnostics    = $routeDiagnostics
        IsDefaultDiagnostic = (-not $verboseDiagnostics -and -not $routeDiagnostics)
    }
}

function Get-BinaryIdentity {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = Require-File -Path $Path
    $item = Get-Item -LiteralPath $fullPath -ErrorAction Stop
    $version = $null
    $mvid = $null
    try { $version = ([System.Reflection.AssemblyName]::GetAssemblyName($fullPath)).Version.ToString() } catch { }
    try { $mvid = ([System.Reflection.Assembly]::ReflectionOnlyLoadFrom($fullPath)).GetModules()[0].ModuleVersionId.ToString() } catch { }

    return [pscustomobject]@{
        Path      = $fullPath
        SizeBytes = $item.Length
        SHA256    = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256 -ErrorAction Stop).Hash
        Version   = $version
        MVID      = $mvid
    }
}

function Test-Sha256Value {
    param([AllowEmptyString()][string] $Value)

    return (-not [string]::IsNullOrWhiteSpace($Value) -and $Value -match '^[A-Fa-f0-9]{64}$')
}

function Get-FileSnapshot {
    param([Parameter(Mandatory = $true)][string] $Path)

    $fullPath = Require-File -Path $Path
    $item = Get-Item -LiteralPath $fullPath -ErrorAction Stop
    return [pscustomobject]@{
        Path             = $fullPath
        Exists           = $true
        SizeBytes        = [long]$item.Length
        SHA256           = (Get-FileHash -LiteralPath $fullPath -Algorithm SHA256 -ErrorAction Stop).Hash
        LastWriteTimeUtc = $item.LastWriteTimeUtc.ToString('o')
    }
}

function Get-RunLogBaseline {
    param([Parameter(Mandatory = $true)][string] $Path)

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return (Get-FileSnapshot -Path $Path)
    }
    if (Test-Path -LiteralPath $Path) {
        throw "Run log path is not a file: $Path"
    }

    return [pscustomobject]@{
        Path             = [System.IO.Path]::GetFullPath($Path)
        Exists           = $false
        SizeBytes        = [long]0
        SHA256           = $null
        LastWriteTimeUtc = $null
    }
}

function Assert-FileRange {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][long] $OffsetBytes,
        [Parameter(Mandatory = $true)][long] $LengthBytes
    )

    $fullPath = Require-File -Path $Path
    $size = (Get-Item -LiteralPath $fullPath -ErrorAction Stop).Length
    if ($OffsetBytes -lt 0 -or $LengthBytes -lt 0 -or $OffsetBytes -gt $size -or $LengthBytes -gt ($size - $OffsetBytes)) {
        throw "File range is outside the file bounds: $fullPath"
    }
    return $fullPath
}

function Get-FileRangeSha256 {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][long] $OffsetBytes,
        [Parameter(Mandatory = $true)][long] $LengthBytes
    )

    $fullPath = Assert-FileRange -Path $Path -OffsetBytes $OffsetBytes -LengthBytes $LengthBytes
    $stream = [System.IO.File]::Open($fullPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $stream.Position = $OffsetBytes
        if ($LengthBytes -eq 0) {
            $hash = $sha256.ComputeHash([byte[]]@())
        } else {
            $buffer = New-Object byte[] 65536
            $remaining = $LengthBytes
            while ($remaining -gt 0) {
                $requested = [int][Math]::Min([long]$buffer.Length, $remaining)
                $read = $stream.Read($buffer, 0, $requested)
                if ($read -le 0) {
                    throw "Unexpected end of file while hashing range: $fullPath"
                }
                $remaining -= $read
                if ($remaining -eq 0) {
                    $hash = $sha256.TransformFinalBlock($buffer, 0, $read)
                } else {
                    $null = $sha256.TransformBlock($buffer, 0, $read, $buffer, 0)
                }
            }
            $hash = $sha256.Hash
        }
        return ([BitConverter]::ToString($hash).Replace('-', ''))
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Test-FileRangeContainsText {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][long] $OffsetBytes,
        [Parameter(Mandatory = $true)][long] $LengthBytes,
        [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string] $Text
    )

    $fullPath = Assert-FileRange -Path $Path -OffsetBytes $OffsetBytes -LengthBytes $LengthBytes
    $stream = [System.IO.File]::Open($fullPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $stream.Position = $OffsetBytes
        $buffer = New-Object byte[] 65536
        $remaining = $LengthBytes
        $carry = ''
        while ($remaining -gt 0) {
            $requested = [int][Math]::Min([long]$buffer.Length, $remaining)
            $read = $stream.Read($buffer, 0, $requested)
            if ($read -le 0) {
                throw "Unexpected end of file while scanning range: $fullPath"
            }
            $remaining -= $read
            $chunk = $carry + [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
            if ($chunk.IndexOf($Text, [System.StringComparison]::Ordinal) -ge 0) {
                return $true
            }
            $carryLength = [Math]::Min([Math]::Max(0, $Text.Length - 1), $chunk.Length)
            $carry = if ($carryLength -eq 0) { '' } else { $chunk.Substring($chunk.Length - $carryLength) }
        }
        return $false
    }
    finally {
        $stream.Dispose()
    }
}

function Find-FileRangeTextOffset {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][long] $OffsetBytes,
        [Parameter(Mandatory = $true)][long] $LengthBytes,
        [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string] $Text,
        [long] $MinimumTextOffset = 0
    )

    if ($MinimumTextOffset -lt 0) {
        throw 'Minimum text offset cannot be negative.'
    }
    $fullPath = Assert-FileRange -Path $Path -OffsetBytes $OffsetBytes -LengthBytes $LengthBytes
    $stream = [System.IO.File]::Open($fullPath, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $stream.Position = $OffsetBytes
        $buffer = New-Object byte[] 65536
        $remaining = $LengthBytes
        $carry = ''
        $textOffset = [long]0
        while ($remaining -gt 0) {
            $requested = [int][Math]::Min([long]$buffer.Length, $remaining)
            $read = $stream.Read($buffer, 0, $requested)
            if ($read -le 0) {
                throw "Unexpected end of file while scanning range: $fullPath"
            }
            $remaining -= $read
            $decoded = [System.Text.Encoding]::UTF8.GetString($buffer, 0, $read)
            $chunk = $carry + $decoded
            $chunkStartOffset = $textOffset - $carry.Length
            $searchIndex = 0
            while ($searchIndex -lt $chunk.Length) {
                $matchIndex = $chunk.IndexOf($Text, $searchIndex, [System.StringComparison]::Ordinal)
                if ($matchIndex -lt 0) { break }
                $matchOffset = $chunkStartOffset + $matchIndex
                if ($matchOffset -ge $MinimumTextOffset) {
                    return $matchOffset
                }
                $searchIndex = $matchIndex + 1
            }
            $textOffset += $decoded.Length
            $carryLength = [Math]::Min([Math]::Max(0, $Text.Length - 1), $chunk.Length)
            $carry = if ($carryLength -eq 0) { '' } else { $chunk.Substring($chunk.Length - $carryLength) }
        }
        return [long]-1
    }
    finally {
        $stream.Dispose()
    }
}

function Get-RunLogEvidence {
    param(
        [Parameter(Mandatory = $true)][string] $Path,
        [Parameter(Mandatory = $true)][object] $Baseline,
        [AllowEmptyString()][string] $RequiredMarker = ''
    )

    $current = Get-FileSnapshot -Path $Path
    foreach ($propertyName in @('Exists', 'SizeBytes', 'SHA256')) {
        if ($null -eq $Baseline.PSObject.Properties[$propertyName]) {
            throw "Run-log baseline is missing $propertyName. Create a new case."
        }
    }

    $baselineExists = $Baseline.Exists -is [bool] -and [bool]$Baseline.Exists
    $baselineSize = [long]$Baseline.SizeBytes
    if ($baselineSize -lt 0) {
        throw 'Run-log baseline has a negative size. Create a new case.'
    }

    $scanOffset = [long]0
    if (-not $baselineExists) {
        if ($baselineSize -ne 0 -or -not [string]::IsNullOrWhiteSpace([string]$Baseline.SHA256)) {
            throw 'Missing run-log baseline must have zero size and no hash. Create a new case.'
        }
        $mode = 'CreatedAfterStart'
    } else {
        if (-not (Test-Sha256Value -Value ([string]$Baseline.SHA256)) -or $baselineSize -lt 0) {
            throw 'Run-log baseline hash is invalid. Create a new case.'
        }
        if ($current.SizeBytes -eq $baselineSize -and $current.SHA256 -eq [string]$Baseline.SHA256) {
            throw "Run log did not change after START: $($current.Path)"
        }

        if ($current.SizeBytes -gt $baselineSize -and
            (Get-FileRangeSha256 -Path $current.Path -OffsetBytes 0 -LengthBytes $baselineSize) -eq [string]$Baseline.SHA256) {
            $scanOffset = $baselineSize
            $mode = 'Appended'
        } else {
            $mode = 'ResetOrReplaced'
        }
    }

    $scanLength = [long]$current.SizeBytes - $scanOffset
    if ($scanLength -le 0) {
        throw "Run log has no current-run content to examine: $($current.Path)"
    }

    $markerFound = $null
    if (-not [string]::IsNullOrWhiteSpace($RequiredMarker)) {
        $markerFound = Test-FileRangeContainsText -Path $current.Path -OffsetBytes $scanOffset -LengthBytes $scanLength -Text $RequiredMarker
    }

    return [pscustomobject]@{
        Path                 = $current.Path
        CurrentSizeBytes     = $current.SizeBytes
        CurrentSHA256        = $current.SHA256
        BaselineExists       = $baselineExists
        BaselineSizeBytes    = $baselineSize
        BaselineSHA256       = if ($baselineExists) { [string]$Baseline.SHA256 } else { $null }
        ScanOffsetBytes      = $scanOffset
        ScanLengthBytes      = $scanLength
        ScanSHA256           = Get-FileRangeSha256 -Path $current.Path -OffsetBytes $scanOffset -LengthBytes $scanLength
        ContinuityMode       = $mode
        RequiredMarker       = $RequiredMarker
        RequiredMarkerFound  = $markerFound
    }
}
