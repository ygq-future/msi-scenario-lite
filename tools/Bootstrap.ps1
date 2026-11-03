<#
.SYNOPSIS
    Start.bat 的一键启动器：准备依赖 → 按需编译 → 启动程序。

.DESCRIPTION
    面向普通用户，不需要打开终端或记住任何命令。双击仓库根目录的 Start.bat 后：

      1. 清理旧版本遗留的无关文件；
      2. 从本机 MSI Center 复制唯一的第三方依赖 MSIWMIACPI2.dll；
      3. 当 MsiScenarioLite.exe 不存在或源码有更新时，用系统自带的 csc.exe 重新编译；
      4. 启动程序；若程序已在托盘运行，则尝试把它切到前台。

    本工具需要管理员权限读写主板 EC，脚本会在需要时自动请求 UAC 提权。
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exePath = Join-Path $root 'MsiScenarioLite.exe'
$sourcePath = Join-Path $root 'MsiScenarioLite.cs'
$buildScript = Join-Path $root 'build.ps1'
$manifestPath = Join-Path $root 'app.manifest'
$iconPath = Join-Path $root 'Assets\app.ico'
$dependencyPath = Join-Path $root 'MSIWMIACPI2.dll'
$stampPath = Join-Path $root '.build-stamp'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Show-ErrorAndPause {
    param([string]$Message)
    Write-Host ''
    Write-Host $Message -ForegroundColor Red
    Write-Host ''
    Read-Host '按 Enter 键退出' | Out-Null
}

function Test-RebuildNeeded {
    if (-not (Test-Path $exePath)) { return $true }
    if (-not (Test-Path $stampPath)) { return $true }
    $stamp = Get-Content -Path $stampPath -Raw -ErrorAction SilentlyContinue
    if ($null -eq $stamp) { return $true }
    return ($stamp.Trim() -ne (Get-BuildFingerprint))
}

function Get-Sha256Hex {
    param([string]$Path)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($Path)
    try {
        $hashBytes = $sha.ComputeHash($stream)
        return -join ($hashBytes | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

function Get-BuildFingerprint {
    # 源码/编译脚本/清单/图标/依赖任一变化都需要重新编译。
    # 用内容哈希而不是文件时间，避免 ZIP 覆盖升级时时间戳不可靠。
    $parts = New-Object System.Text.StringBuilder
    foreach ($inputPath in @($sourcePath, $buildScript, $manifestPath, $iconPath, $dependencyPath)) {
        $name = Split-Path $inputPath -Leaf
        if (-not (Test-Path $inputPath)) {
            [void]$parts.Append($name).Append(':missing;')
            continue
        }
        [void]$parts.Append($name).Append(':').Append((Get-Sha256Hex -Path $inputPath)).Append(';')
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($parts.ToString())
        return -join ($sha.ComputeHash($bytes) | ForEach-Object { $_.ToString('x2') })
    }
    finally {
        $sha.Dispose()
    }
}

function Get-RunningAppProcesses {
    $result = @()
    foreach ($process in @(Get-Process -Name 'MsiScenarioLite' -ErrorAction SilentlyContinue)) {
        try {
            if ($process.Path -eq $exePath) { $result += $process }
        }
        catch { }
    }
    return $result
}

function Show-AppWindow {
    param([int]$ProcessId)

    if (-not ('ScenarioLite.NativeWindow' -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Text;
using System.Runtime.InteropServices;

namespace ScenarioLite {
    public static class NativeWindow {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
"@
    }

    $script:foundWindow = [IntPtr]::Zero
    $script:targetProcessId = $ProcessId
    $callback = [ScenarioLite.NativeWindow+EnumWindowsProc]{
        param($hWnd, $lParam)
        $windowProcessId = [uint32]0
        [ScenarioLite.NativeWindow]::GetWindowThreadProcessId($hWnd, [ref]$windowProcessId) | Out-Null
        if ($windowProcessId -eq [uint32]$script:targetProcessId) {
            $title = New-Object System.Text.StringBuilder 256
            [ScenarioLite.NativeWindow]::GetWindowText($hWnd, $title, $title.Capacity) | Out-Null
            if ($title.ToString() -eq 'MSI Scenario Lite') {
                $script:foundWindow = $hWnd
                return $false
            }
        }
        return $true
    }
    [ScenarioLite.NativeWindow]::EnumWindows($callback, [IntPtr]::Zero) | Out-Null

    if ($script:foundWindow -eq [IntPtr]::Zero) { return $false }

    [ScenarioLite.NativeWindow]::ShowWindow($script:foundWindow, 5) | Out-Null   # SW_SHOW
    [ScenarioLite.NativeWindow]::ShowWindow($script:foundWindow, 9) | Out-Null   # SW_RESTORE
    [ScenarioLite.NativeWindow]::SetForegroundWindow($script:foundWindow) | Out-Null
    return [ScenarioLite.NativeWindow]::IsWindowVisible($script:foundWindow)
}

if (-not (Test-Administrator)) {
    Write-Host '正在请求管理员权限（读写主板 EC 需要管理员，请在 UAC 中选择“是”）...' -ForegroundColor Yellow
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
        )
    }
    catch {
        Write-Host ''
        Write-Host '已取消管理员授权，无法启动。' -ForegroundColor Red
        Read-Host '按 Enter 键退出' | Out-Null
        exit 1
    }
    exit 0
}

try {
    $Host.UI.RawUI.WindowTitle = 'MSI Scenario Lite 启动器'
}
catch { }

Write-Host ''
Write-Host '  MSI Scenario Lite 启动器' -ForegroundColor Cyan
Write-Host '  --------------------------------' -ForegroundColor DarkGray
Write-Host ''

Write-Host '[1/3] 检查运行依赖...' -ForegroundColor White
try {
& (Join-Path $PSScriptRoot 'Prepare-Dependencies.ps1')

$running = @(Get-RunningAppProcesses)
$rebuildNeeded = Test-RebuildNeeded

if ($running.Count -gt 0) {
    if ($rebuildNeeded) {
        Show-ErrorAndPause @'
检测到程序正在运行，而源码/依赖与当前 exe 不一致，无法覆盖编译。

请先从右下角托盘图标右键选择“完全退出程序”，然后重新双击 Start.bat。
'@
        exit 1
    }
    if (Show-AppWindow -ProcessId $running[0].Id) {
        Write-Host ''
        Write-Host '[OK] 程序已在运行，已切换到前台。' -ForegroundColor Green
    }
    else {
        Write-Host ''
        Write-Host '[!] 程序已在后台运行（托盘），请双击右下角托盘图标打开。' -ForegroundColor Yellow
    }
    exit 0
}

Write-Host ''
Write-Host '[2/3] 检查程序文件...' -ForegroundColor White
if ($rebuildNeeded) {
    Write-Host '  正在编译（首次启动或源码有更新）...' -ForegroundColor Gray
    & $buildScript
    if (-not (Test-Path $exePath)) {
        throw "编译结束但未找到 $exePath。"
    }
    Set-Content -Path $stampPath -Value (Get-BuildFingerprint) -Encoding ASCII
    Write-Host '  编译完成。' -ForegroundColor Green
}
else {
    Write-Host '  程序已是最新，无需编译。' -ForegroundColor Green
}

Write-Host ''
Write-Host '[3/3] 启动程序...' -ForegroundColor White
Start-Process -FilePath $exePath -WorkingDirectory $root
Write-Host '  已启动，本窗口将自动关闭。' -ForegroundColor Green
Start-Sleep -Milliseconds 300
}
catch {
    Show-ErrorAndPause ("启动失败：`n" + $_.Exception.Message)
    exit 1
}
