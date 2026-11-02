<#
.SYNOPSIS
    WHEA 硬件错误实时弹窗告警 / 一键安装 / 卸载 / 测试。

.DESCRIPTION
    监听 Windows 系统日志中的 Microsoft-Windows-WHEA-Logger 事件（CPU 缓存 / 核心
    硬件错误，通常由 Curve Optimizer 负压过深或内存不稳引起），一旦出现就立刻在
    桌面弹出置顶警告，避免"悄悄报错、最后突然黑屏重启"。

    默认（无参数）：弹出最近一条 WHEA 事件 —— 这是计划任务调用的模式，平时 0 内存占用。
    -Install      ：注册/更新计划任务（自动请求管理员权限）
    -Uninstall    ：删除计划任务（自动请求管理员权限）
    -Test         ：弹一个示例告警，确认弹窗能正常显示

.EXAMPLE
    # 一键安装（推荐：右键"使用 PowerShell 运行"，或在终端里执行）
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\WHEA-Alert.ps1 -Install

.EXAMPLE
    # 卸载
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\WHEA-Alert.ps1 -Uninstall

.EXAMPLE
    # 测试弹窗
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\WHEA-Alert.ps1 -Test

.NOTES
    计划任务名：WHEA_Hardware_Error_Alert
    触发器    ：事件 ID 18 / 19（含 17、20 等同类），来源 Microsoft-Windows-WHEA-Logger，日志 System
    运行身份  ：当前登录用户（InteractiveToken），最低权限即可读取系统日志
    若不喜欢命令行，也可在"事件查看器 → Windows 日志 → 系统"中右键任意 WHEA-Logger
    事件 → "将任务附加到此事件" → 启动程序 → 程序填 powershell.exe，
    参数填：-WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File "<本脚本完整路径>"
#>
[CmdletBinding(DefaultParameterSetName = 'Show')]
param(
    [Parameter(ParameterSetName = 'Install')]
    [switch]$Install,

    [Parameter(ParameterSetName = 'Uninstall')]
    [switch]$Uninstall,

    [Parameter(ParameterSetName = 'Test')]
    [switch]$Test,

    [string]$TaskName = 'WHEA_Hardware_Error_Alert'
)

$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restart-Elevated {
    param([string[]]$Arguments)

    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"") + $Arguments
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -Verb RunAs -Wait
}

function Show-Alert {
    param([string]$Title, [string]$Body)

    Add-Type -AssemblyName PresentationFramework
    [System.Windows.MessageBox]::Show(
        $Body,
        $Title,
        [System.Windows.MessageBoxButton]::OK,
        [System.Windows.MessageBoxImage]::Warning,
        [System.Windows.MessageBoxResult]::OK,
        [System.Windows.MessageBoxOptions]::ServiceNotification
    ) | Out-Null
}

switch ($PSCmdlet.ParameterSetName) {

    'Test' {
        Show-Alert -Title 'WHEA 硬件错误实时告警（测试）' -Body @"
这是一条测试告警。

如果你看到这个窗口，说明弹窗链路正常。
真实触发时，这里会显示 WHEA 事件 ID、发生时间与完整错误描述。

建议立即保存当前工作，并检查 BIOS 中的 Curve Optimizer 负压设置。
"@
    }

    'Install' {
        if (-not (Test-Path $PSCommandPath)) {
            throw "无法定位脚本自身路径：$PSCommandPath"
        }

        # 事件订阅：所有 WHEA-Logger 事件（涵盖 17/18/19/20 等 ID）
        $subscription = '<QueryList><Query Id="0" Path="System"><Select Path="System">*[System[Provider[@Name=''Microsoft-Windows-WHEA-Logger'']]]</Select></Query></QueryList>'

        $taskXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>当系统发生 WHEA-Logger（CPU 缓存/核心硬件错误）时立即置顶弹窗告警。</Description>
  </RegistrationInfo>
  <Triggers>
    <EventTrigger>
      <Enabled>true</Enabled>
      <Subscription>$([System.Security.SecurityElement]::Escape($subscription))</Subscription>
    </EventTrigger>
  </Triggers>
  <Principals>
    <Principal id="Author">
      <LogonType>InteractiveToken</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>false</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
    <Priority>4</Priority>
  </Settings>
  <Actions Context="Author">
    <Exec>
      <Command>powershell.exe</Command>
      <Arguments>-WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File "$PSCommandPath"</Arguments>
    </Exec>
  </Actions>
</Task>
"@

        $tempXml = Join-Path $env:TEMP "whea-alert-task-$([guid]::NewGuid().ToString('N')).xml"
        $registered = $false
        $lastError = $null
        try {
            # schtasks 需要带 BOM 的 UTF-16LE
            [System.IO.File]::WriteAllText($tempXml, $taskXml, [System.Text.Encoding]::Unicode)
            $output = & schtasks.exe /Create /TN $TaskName /XML $tempXml /F 2>&1
            $registered = ($LASTEXITCODE -eq 0)
            if (-not $registered) { $lastError = ($output | Out-String).Trim() }
        }
        catch {
            $lastError = $_.Exception.Message
        }
        finally {
            Remove-Item $tempXml -Force -ErrorAction SilentlyContinue
        }

        if (-not $registered) {
            # 普通权限下注册到根目录通常也可行；失败再提权重试一次
            if (-not (Test-Administrator)) {
                Write-Host '注册计划任务需要管理员权限，正在请求提权...' -ForegroundColor Yellow
                Restart-Elevated -Arguments @('-Install')
                return
            }
            throw "注册计划任务失败：$lastError"
        }

        Write-Host ''
        Write-Host "[OK] 已安装计划任务：$TaskName" -ForegroundColor Green
        Write-Host "  脚本路径：$PSCommandPath"
        Write-Host '  触发条件：System 日志出现 Microsoft-Windows-WHEA-Logger 事件'
        Write-Host ''
        Write-Host '后续操作：' -ForegroundColor Cyan
        Write-Host "  测试弹窗  powershell -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Test"
        Write-Host "  卸载      powershell -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Uninstall"
        Write-Host '  图形界面  任务计划程序 → 任务计划程序库 → WHEA_Hardware_Error_Alert（可禁用/删除）'
    }

    'Uninstall' {
        $output = & schtasks.exe /Delete /TN $TaskName /F 2>&1
        if ($LASTEXITCODE -ne 0) {
            if (-not (Test-Administrator)) {
                Write-Host '删除计划任务需要管理员权限，正在请求提权...' -ForegroundColor Yellow
                Restart-Elevated -Arguments @('-Uninstall')
                return
            }
            Write-Warning "删除计划任务失败（可能本就不存在）：$($output | Out-String)"
            return
        }
        Write-Host "[OK] 已删除计划任务：$TaskName" -ForegroundColor Green
    }

    default {
        # 计划任务调用模式：读取最近一条 WHEA 事件并弹窗
        $event = Get-WinEvent -FilterHashtable @{
            LogName      = 'System'
            ProviderName = 'Microsoft-Windows-WHEA-Logger'
        } -MaxEvents 1 -ErrorAction SilentlyContinue

        if (-not $event) { return }

        # 只对本次开机后发生的事件告警，避免开机时把历史事件又弹一遍
        $bootTime = (Get-CimInstance Win32_OperatingSystem).LastBootUpTime
        if ($event.TimeCreated -lt $bootTime) { return }

        $body = @"
检测到 CPU 硬件错误（WHEA-Logger，事件 ID $($event.Id)）
发生时间：$($event.TimeCreated)

$($event.Message)

建议立即保存当前工作，并检查 BIOS 中的 Curve Optimizer 负压设置。
（CCD 共享 L3 缓存被低载低电压拖垮时，通常会先连续出现本警告，最后才硬重启。）
"@
        Show-Alert -Title 'WHEA 硬件错误实时告警' -Body $body
    }
}
