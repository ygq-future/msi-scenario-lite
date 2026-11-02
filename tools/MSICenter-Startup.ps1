<#
.SYNOPSIS
    启用 / 禁用 MSI Center 的 UWP 启动项（Windows 设置 → 应用 → 启动 里那一项）。

.DESCRIPTION
    原版 MSI Center 除了 3 个后台服务和 2 个计划任务之外，还注册了一个 UWP
    StartupTask（TaskId: MSI_Center_UWP）。它由 AppX 包声明，**不受服务/计划任务
    的禁用影响**，所以在"设置 → 应用 → 启动"里会一直显示为"开"。

    其状态保存在（每用户，无需管理员）：
      HKCU\Software\Classes\Local Settings\Software\Microsoft\Windows\
        CurrentVersion\AppModel\SystemAppData\<PackageFamilyName>\MSI_Center_UWP
        State (DWORD)

    State 取值（StartupTaskState）：
      0 = Disabled            已禁用
      1 = DisabledByUser      用户手动禁用（设置界面显示"关"）
      2 = Enabled             已启用（设置界面显示"开"）
      3 = DisabledByPolicy    被组策略禁用
      4 = EnabledByPolicy     被组策略启用

.PARAMETER Disable
    将启动项设为 DisabledByUser（State=1）。

.PARAMETER Enable
    将启动项恢复为 Enabled（State=2）。

.PARAMETER Status
    仅查看当前状态（默认）。

.EXAMPLE
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\MSICenter-Startup.ps1 -Disable
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Disable')]
    [switch]$Disable,

    [Parameter(ParameterSetName = 'Enable')]
    [switch]$Enable,

    [Parameter(ParameterSetName = 'Status')]
    [switch]$Status
)

$ErrorActionPreference = 'Stop'

$stateNames = @{
    0 = 'Disabled'
    1 = 'DisabledByUser'
    2 = 'Enabled'
    3 = 'DisabledByPolicy'
    4 = 'EnabledByPolicy'
}

$taskId = 'MSI_Center_UWP'
$package = Get-AppxPackage -Name '*MSICenter*' -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $package) {
    Write-Host '未检测到 MSI Center（AppX 包未安装），无需处理启动项。' -ForegroundColor Yellow
    exit 0
}

$keyPath = "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$($package.PackageFamilyName)\$taskId"

function Get-StartupState {
    if (-not (Test-Path $keyPath)) { return $null }
    $value = (Get-ItemProperty -Path $keyPath -Name 'State' -ErrorAction SilentlyContinue).State
    if ($null -eq $value) { return $null }
    return [int]$value
}

function Format-State {
    param($state)
    if ($null -eq $state) { return '未注册（将按需创建）' }
    $name = $stateNames[[int]$state]
    if (-not $name) { $name = 'Unknown' }
    return "$state ($name)"
}

function Set-StartupState {
    param([int]$state)

    if (-not (Test-Path $keyPath)) {
        New-Item -Path $keyPath -Force | Out-Null
    }
    Set-ItemProperty -Path $keyPath -Name 'State' -Value $state -Type DWord
    if (-not (Get-ItemProperty -Path $keyPath -Name 'UserEnabledStartupOnce' -ErrorAction SilentlyContinue)) {
        Set-ItemProperty -Path $keyPath -Name 'UserEnabledStartupOnce' -Value 0 -Type DWord
    }
}

switch ($PSCmdlet.ParameterSetName) {

    'Disable' {
        $before = Get-StartupState
        Set-StartupState -state 1
        $after = Get-StartupState
        Write-Host "[OK] MSI Center UWP 启动项已禁用" -ForegroundColor Green
        Write-Host "  包名：$($package.PackageFamilyName)"
        Write-Host "  状态：$(Format-State $before)  ->  $(Format-State $after)"
        Write-Host '  设置 → 应用 → 启动 中的 "MSI Center" 现在应显示为"关"（重新打开设置页即可看到）。'
    }

    'Enable' {
        $before = Get-StartupState
        Set-StartupState -state 2
        $after = Get-StartupState
        Write-Host "[OK] MSI Center UWP 启动项已恢复启用" -ForegroundColor Green
        Write-Host "  包名：$($package.PackageFamilyName)"
        Write-Host "  状态：$(Format-State $before)  ->  $(Format-State $after)"
    }

    default {
        $current = Get-StartupState
        Write-Host "MSI Center UWP 启动项" -ForegroundColor Cyan
        Write-Host "  包名：$($package.PackageFamilyName)"
        Write-Host "  注册表：$keyPath"
        Write-Host "  当前状态：$(Format-State $current)"
    }
}
