<#
.SYNOPSIS
    准备 MSI Scenario Lite 的运行时依赖，并清理旧版本遗留的无关文件。

.DESCRIPTION
    本项目不随仓库分发微星的专有二进制文件。脚本会从本机已安装的 MSI Center
    中复制唯一需要的 MSIWMIACPI2.dll（不下载任何内容），并删除旧版本可能残留的
    ResetUEFIVariable.exe / UEFIVaribleDll.dll（Win/Fn 键位互换工具，与 EC / MUX
    功能无关）。

    该脚本由仓库根目录的 Start.bat 自动调用，也可以手动运行。

.PARAMETER MsiCenterDir
    手动指定 MSI Center 安装目录；省略时自动从常见路径与卸载注册表项中查找。

.PARAMETER Force
    即使目标目录已存在 MSIWMIACPI2.dll 也重新复制（MSI Center 升级后可用）。
#>
[CmdletBinding()]
param(
    [string]$MsiCenterDir,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dependencyName = 'MSIWMIACPI2.dll'
$dependencyPath = Join-Path $root $dependencyName

# 旧版本曾要求把这两个文件复制到根目录，它们只服务于 Win/Fn 键位互换与卸载还原。
$legacyNames = @('ResetUEFIVariable.exe', 'UEFIVaribleDll.dll', 'UEFIVariableDll.dll')
foreach ($name in $legacyNames) {
    $legacyPath = Join-Path $root $name
    if (Test-Path $legacyPath) {
        Remove-Item $legacyPath -Force
        Write-Host "已清理旧版残留文件：$name" -ForegroundColor Yellow
    }
}

if ((Test-Path $dependencyPath) -and -not $Force) {
    Write-Host "依赖已就绪：$dependencyName" -ForegroundColor Green
    return
}

function Find-MsiCenterDir {
    $candidates = New-Object System.Collections.Generic.List[string]
    if ($MsiCenterDir) { $candidates.Add($MsiCenterDir) }
    if (${env:ProgramFiles(x86)}) {
        $candidates.Add((Join-Path ${env:ProgramFiles(x86)} 'MSI\MSI Center'))
    }
    if ($env:ProgramFiles) {
        $candidates.Add((Join-Path $env:ProgramFiles 'MSI\MSI Center'))
    }

    foreach ($uninstallRoot in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')) {
        Get-ChildItem -Path $uninstallRoot -ErrorAction SilentlyContinue | ForEach-Object {
            $props = Get-ItemProperty -Path $_.PSPath -ErrorAction SilentlyContinue
            if ($props.DisplayName -like '*MSI Center*' -and $props.InstallLocation) {
                $candidates.Add($props.InstallLocation.TrimEnd('\'))
            }
        }
    }

    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if (-not $candidate) { continue }
        if (Test-Path (Join-Path $candidate $dependencyName)) {
            return $candidate
        }
    }
    return $null
}

$sourceDir = Find-MsiCenterDir
if (-not $sourceDir) {
    throw @"
未找到 $dependencyName。

本工具需要本机安装的 MSI Center 提供的 $dependencyName（仓库不直接分发该文件）：
  1. 安装 MSI Center 后重新双击 Start.bat；或
  2. 手动把 $dependencyName 复制到：$root
"@
}

Copy-Item (Join-Path $sourceDir $dependencyName) $dependencyPath -Force
Write-Host "已从 MSI Center 复制依赖：$dependencyName" -ForegroundColor Green
Write-Host "  来源：$sourceDir"
Write-Host '  该文件受微星授权限制，请勿再分发。' -ForegroundColor DarkGray
