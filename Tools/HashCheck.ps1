<#
.SYNOPSIS
    SteamP2PFriends 双机测试 - 双端哈希值自动校验脚本
.DESCRIPTION
    自动查找 Unturned 安装路径（从 Steam 注册表 + libraryfolders.vdf + 全盘扫描），
    递归搜索关键文件（Unturned 的 Bundles 目录结构不规则），输出标准化 JSON。
    适用于 Windows PowerShell 5.1 及以上。
.NOTES
    文件名: HashCheck.ps1
    用途: 第十次双机测试前置条件 - 6 文件 SHA-256 比对
    比对目标:
      1. buildid（从 appmanifest_304930.acf 读取）
      2. Assembly-CSharp.dll
      3. core.masterbundle
      4. core.masterbundle.hash
      5. Spec_Ops_Bottom.dat
      6. PEI_Flowers_00_Foliage.dat
#>

$ErrorActionPreference = "SilentlyContinue"

function Find-UnturnedPath {
    # 方式 1：从 Steam 注册表读取 SteamPath，再解析 libraryfolders.vdf
    $steamReg = Get-ItemProperty -Path "HKCU:\Software\Valve\Steam" -ErrorAction SilentlyContinue
    if ($steamReg -and $steamReg.SteamPath) {
        $steamPath = $steamReg.SteamPath
        $libraryFile = Join-Path $steamPath "steamapps\libraryfolders.vdf"
        if (Test-Path $libraryFile) {
            $content = Get-Content $libraryFile -Raw -ErrorAction SilentlyContinue
            if ($content) {
                $pathMatches = [regex]::Matches($content, '"path"\s+"([^"]+)"')
                foreach ($m in $pathMatches) {
                    $p = $m.Groups[1].Value -replace '\\\\', '\'
                    $candidate = Join-Path $p "steamapps\common\Unturned"
                    if (Test-Path $candidate) { return $candidate }
                }
            }
        }
        # 直接尝试 Steam 安装目录下的默认路径
        $default = Join-Path $steamPath "steamapps\common\Unturned"
        if (Test-Path $default) { return $default }
    }

    # 方式 2：扫描所有盘符的常见 Steam 安装位置
    $drives = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Used -gt 0 }
    foreach ($d in $drives) {
        $candidates = @(
            "$($d.Root)Steam\steamapps\common\Unturned",
            "$($d.Root)Program Files (x86)\Steam\steamapps\common\Unturned",
            "$($d.Root)Program Files\Steam\steamapps\common\Unturned",
            "$($d.Root)Games\Steam\steamapps\common\Unturned",
            "$($d.Root)SteamLibrary\steamapps\common\Unturned"
        )
        foreach ($c in $candidates) {
            if (Test-Path $c) { return $c }
        }
    }

    return $null
}

function Find-FileRecursive {
    param([string]$root, [string]$pattern)
    $found = Get-ChildItem -Path $root -Recurse -Filter $pattern -File -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { return $found.FullName }
    return $null
}

function Get-BuildId {
    param([string]$root)
    # Unturned Steam App ID = 304930
    # appmanifest_304930.acf 位于 steamapps 目录
    $steamapps = Split-Path (Split-Path $root)
    $manifest = Join-Path $steamapps "appmanifest_304930.acf"
    if (Test-Path $manifest) {
        $content = Get-Content $manifest -Raw -ErrorAction SilentlyContinue
        if ($content) {
            $match = [regex]::Match($content, '"buildid"\s+"(\d+)"')
            if ($match.Success) {
                return $match.Groups[1].Value
            }
        }
    }
    # Fallback：全盘搜索 appmanifest_304930.acf
    $drives = Get-PSDrive -PSProvider FileSystem | Where-Object { $_.Used -gt 0 }
    foreach ($d in $drives) {
        $found = Get-ChildItem -Path $d.Root -Recurse -Filter "appmanifest_304930.acf" -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($found) {
            $content = Get-Content $found.FullName -Raw -ErrorAction SilentlyContinue
            if ($content) {
                $match = [regex]::Match($content, '"buildid"\s+"(\d+)"')
                if ($match.Success) {
                    return $match.Groups[1].Value
                }
            }
        }
    }
    return "NOT_FOUND"
}

function Get-FileHashSafe {
    param([string]$path)
    if (-not $path -or -not (Test-Path $path)) { return "FILE_NOT_FOUND" }
    try {
        return (Get-FileHash -Path $path -Algorithm SHA256).Hash
    } catch {
        return "ERROR: $($_.Exception.Message)"
    }
}

# ===== 主流程 =====
Write-Host ""
Write-Host "=== SteamP2PFriends 双端哈希值自动校验 ===" -ForegroundColor Cyan
Write-Host "时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host "机器: $env:COMPUTERNAME"
Write-Host ""

# 步骤 1：查找 Unturned 安装路径
Write-Host "[1/3] 正在查找 Unturned 安装路径..." -ForegroundColor Yellow
$unturnedPath = Find-UnturnedPath
if (-not $unturnedPath) {
    Write-Host "[!] 未找到 Unturned 安装路径" -ForegroundColor Red
    Write-Host "    已尝试：" -ForegroundColor Gray
    Write-Host "    - 从 Steam 注册表读取 (HKCU:\Software\Valve\Steam)" -ForegroundColor Gray
    Write-Host "    - 解析 libraryfolders.vdf" -ForegroundColor Gray
    Write-Host "    - 全盘扫描 Steam 常见安装位置" -ForegroundColor Gray
    Write-Host ""
    Write-Host "    请手动确认 Unturned 安装位置后，修改脚本中的 `$unturnedPath 变量。" -ForegroundColor Gray
    Read-Host "按回车退出"
    exit 1
}
Write-Host "[+] Unturned 安装路径: $unturnedPath" -ForegroundColor Green
Write-Host ""

# 步骤 2：定位所有目标文件
Write-Host "[2/3] 正在定位目标文件..." -ForegroundColor Yellow

$targets = @(
    @{ Name = "buildid";                      Type = "manifest" }
    @{ Name = "Assembly-CSharp.dll";          Type = "file";       RelativePath = "Unturned_Data\Managed\Assembly-CSharp.dll" }
    @{ Name = "core.masterbundle";            Type = "recursive";  Pattern = "core.masterbundle" }
    @{ Name = "core.masterbundle.hash";       Type = "recursive";  Pattern = "core.masterbundle.hash" }
    @{ Name = "Spec_Ops_Bottom.dat";          Type = "recursive";  Pattern = "Spec_Ops_Bottom.dat" }
    @{ Name = "PEI_Flowers_00_Foliage.dat";   Type = "recursive";  Pattern = "PEI_Flowers_00_Foliage.dat" }
)

$results = @()

foreach ($t in $targets) {
    $path = "NOT_FOUND"
    $hash = "NOT_FOUND"
    $status = "MISS"

    switch ($t.Type) {
        "manifest" {
            $buildId = Get-BuildId -root $unturnedPath
            $path = "(appmanifest_304930.acf)"
            $hash = $buildId
            if ($buildId -ne "NOT_FOUND") { $status = "OK  " }
        }
        "file" {
            $fullPath = Join-Path $unturnedPath $t.RelativePath
            if (Test-Path $fullPath) {
                $path = $fullPath
                $hash = Get-FileHashSafe -path $fullPath
                if ($hash -ne "FILE_NOT_FOUND" -and $hash -notlike "ERROR*") { $status = "OK  " }
            } else {
                # Fallback：递归搜索
                $found = Find-FileRecursive -root $unturnedPath -pattern $t.Name
                if ($found) {
                    $path = $found
                    $hash = Get-FileHashSafe -path $found
                    if ($hash -ne "FILE_NOT_FOUND" -and $hash -notlike "ERROR*") { $status = "OK*" }
                }
            }
        }
        "recursive" {
            $found = Find-FileRecursive -root $unturnedPath -pattern $t.Pattern
            if ($found) {
                $path = $found
                $hash = Get-FileHashSafe -path $found
                if ($hash -ne "FILE_NOT_FOUND" -and $hash -notlike "ERROR*") { $status = "OK  " }
            }
        }
    }

    Write-Host ("  [{0}] {1,-30} -> {2}" -f $status, $t.Name, $path) -ForegroundColor $(if ($status -like "OK*") { 'Green' } else { 'Red' })

    $results += [PSCustomObject]@{
        Machine    = $env:COMPUTERNAME
        File       = $t.Name
        Status     = $status.Trim()
        SHA256     = $hash
        Path       = $path
        Timestamp  = (Get-Date -Format "yyyy-MM-dd HH:mm:ss")
    }
}

Write-Host ""

# 步骤 3：输出表格 + 保存 JSON
Write-Host "[3/3] 结果汇总..." -ForegroundColor Yellow
Write-Host ""
Write-Host "[$env:COMPUTERNAME] 哈希值结果：" -ForegroundColor Cyan
$results | Format-Table -AutoSize -Wrap

# 保存 JSON 到桌面（方便用户找到）
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$jsonFileName = "unturned_hashes_${env:COMPUTERNAME}_$timestamp.json"
$jsonPath = Join-Path $env:USERPROFILE "Desktop\$jsonFileName"
$results | ConvertTo-Json -Depth 3 | Out-File -FilePath $jsonPath -Encoding utf8
Write-Host ""
Write-Host "[+] JSON 结果已保存: $jsonPath" -ForegroundColor Green
Write-Host ""
Write-Host "请将此 JSON 文件回传给 Claude，进行双端比对。" -ForegroundColor Cyan
Write-Host ""
Read-Host "按回车退出"
