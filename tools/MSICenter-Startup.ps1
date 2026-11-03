<#
.SYNOPSIS
    启用 / 禁用 MSI Center 的 UWP 启动项（Windows 设置 → 应用 → 启动 里那一项）。

.DESCRIPTION
    原版 MSI Center 除了 3 个后台服务和 2 个计划任务之外，还注册了一个 UWP
    StartupTask（TaskId: MSI_Center_UWP）。它由 AppX 包声明，**不受服务/计划任务
    的禁用影响**，所以在"设置 → 应用 → 启动"里会一直显示为"开"。

    普通用户请直接双击 tools\MSICenter-Startup.bat，在菜单里选择操作。

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

.PARAMETER Menu
    显示交互式菜单，供双击 MSICenter-Startup.bat 时使用。
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Disable')]
    [switch]$Disable,

    [Parameter(ParameterSetName = 'Enable')]
    [switch]$Enable,

    [Parameter(ParameterSetName = 'Status')]
    [switch]$Status,

    [Parameter(ParameterSetName = 'Menu', Mandatory = $true)]
    [switch]$Menu
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

function Get-CenterPackage {
    return Get-AppxPackage -Name '*MSICenter*' -ErrorAction SilentlyContinue | Select-Object -First 1
}

function Get-StartupKeyPath {
    param($Package)
    return "HKCU:\Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\$($Package.PackageFamilyName)\$taskId"
}

function Get-StartupState {
    param([string]$KeyPath)
    if (-not (Test-Path $KeyPath)) { return $null }
    $value = (Get-ItemProperty -Path $KeyPath -Name 'State' -ErrorAction SilentlyContinue).State
    if ($null -eq $value) { return $null }
    return [int]$value
}

function Format-State {
    param($State)
    if ($null -eq $State) { return '未注册（将按需创建）' }
    $name = $stateNames[[int]$State]
    if (-not $name) { $name = 'Unknown' }
    return "$State ($name)"
}

function Set-StartupState {
    param([string]$KeyPath, [int]$State)

    if (-not (Test-Path $KeyPath)) {
        New-Item -Path $KeyPath -Force | Out-Null
    }
    Set-ItemProperty -Path $KeyPath -Name 'State' -Value $State -Type DWord
    if (-not (Get-ItemProperty -Path $KeyPath -Name 'UserEnabledStartupOnce' -ErrorAction SilentlyContinue)) {
        Set-ItemProperty -Path $KeyPath -Name 'UserEnabledStartupOnce' -Value 0 -Type DWord
    }
}

function Show-StartupStatus {
    $package = Get-CenterPackage
    if (-not $package) {
        Write-Host '未检测到 MSI Center（AppX 包未安装），无需处理启动项。' -ForegroundColor Yellow
        return
    }
    $keyPath = Get-StartupKeyPath -Package $package
    Write-Host 'MSI Center UWP 启动项' -ForegroundColor Cyan
    Write-Host "  包名：$($package.PackageFamilyName)"
    Write-Host "  注册表：$keyPath"
    Write-Host "  当前状态：$(Format-State (Get-StartupState -KeyPath $keyPath))"
}

function Disable-StartupTask {
    $package = Get-CenterPackage
    if (-not $package) {
        Write-Host '未检测到 MSI Center（AppX 包未安装），无需处理启动项。' -ForegroundColor Yellow
        return
    }
    $keyPath = Get-StartupKeyPath -Package $package
    $before = Get-StartupState -KeyPath $keyPath
    Set-StartupState -KeyPath $keyPath -State 1
    $after = Get-StartupState -KeyPath $keyPath
    Write-Host '[OK] MSI Center UWP 启动项已禁用' -ForegroundColor Green
    Write-Host "  包名：$($package.PackageFamilyName)"
    Write-Host "  状态：$(Format-State $before)  ->  $(Format-State $after)"
    Write-Host '  设置 → 应用 → 启动 中的 "MSI Center" 现在应显示为"关"（重新打开设置页即可看到）。'
}

function Enable-StartupTask {
    $package = Get-CenterPackage
    if (-not $package) {
        Write-Host '未检测到 MSI Center（AppX 包未安装），无需处理启动项。' -ForegroundColor Yellow
        return
    }
    $keyPath = Get-StartupKeyPath -Package $package
    $before = Get-StartupState -KeyPath $keyPath
    Set-StartupState -KeyPath $keyPath -State 2
    $after = Get-StartupState -KeyPath $keyPath
    Write-Host '[OK] MSI Center UWP 启动项已恢复启用' -ForegroundColor Green
    Write-Host "  包名：$($package.PackageFamilyName)"
    Write-Host "  状态：$(Format-State $before)  ->  $(Format-State $after)"
}

function Invoke-Menu {
    while ($true) {
        try { Clear-Host } catch { }
        Write-Host '============================================================' -ForegroundColor Cyan
        Write-Host '  MSI Center UWP 启动项（设置 → 应用 → 启动）' -ForegroundColor Cyan
        Write-Host '============================================================' -ForegroundColor Cyan
        Write-Host '  1. 查看当前状态'
        Write-Host '  2. 禁用 MSI Center 启动项'
        Write-Host '  3. 恢复 MSI Center 启动项'
        Write-Host '  0. 退出'
        Write-Host ''
        $choice = Read-Host '请输入数字后回车'
        switch ($choice) {
            '1' {
                Show-StartupStatus
                Write-Host ''
                Read-Host '按 Enter 返回菜单' | Out-Null
            }
            '2' {
                Disable-StartupTask
                Write-Host ''
                Read-Host '按 Enter 返回菜单' | Out-Null
            }
            '3' {
                Enable-StartupTask
                Write-Host ''
                Read-Host '按 Enter 返回菜单' | Out-Null
            }
            '0' { return }
            default {
                Write-Host '无效输入，请重新选择。' -ForegroundColor Yellow
                Start-Sleep -Milliseconds 900
            }
        }
    }
}

switch ($PSCmdlet.ParameterSetName) {
    'Disable' { Disable-StartupTask }
    'Enable'  { Enable-StartupTask }
    'Menu'    { Invoke-Menu }
    default   { Show-StartupStatus }
}
